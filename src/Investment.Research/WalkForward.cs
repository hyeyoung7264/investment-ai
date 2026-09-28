using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Domain.Research;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Investment.Strategies;
using Investment.Strategies.Control;
using Microsoft.EntityFrameworkCore;

namespace Investment.Research;

public sealed record Fold(int Index, DateOnly TrainStart, DateOnly TrainEnd, DateOnly ValidationStart, DateOnly ValidationEnd, DateOnly OosStart, DateOnly OosEnd);

/// <summary>
/// Rolling walk-forward: train [Y, Y+T), validation [Y+T, Y+T+V), out-of-sample [Y+T+V, Y+T+V+O), stepping S years.
/// Parameters are chosen on train only; a fold is traded out-of-sample only if its validation net EV is positive.
/// </summary>
public sealed record WalkForwardPlan
{
    public required DateOnly FirstTrainStart { get; init; }
    public required DateOnly LastDate { get; init; }
    public int TrainYears { get; init; } = 3;
    public int ValidationYears { get; init; } = 1;
    public int OosYears { get; init; } = 1;
    public int StepYears { get; init; } = 1;

    /// <summary>Pre-declared parameter sets (JSON; null = defaults). Every entry counts as a variant tried.</summary>
    public IReadOnlyList<string?> ParameterGrid { get; init; } = [null];

    public IReadOnlyList<Fold> Folds()
    {
        var folds = new List<Fold>();
        for (var y = FirstTrainStart; ; y = y.AddYears(StepYears))
        {
            var valStart = y.AddYears(TrainYears);
            var oosStart = valStart.AddYears(ValidationYears);
            if (oosStart > LastDate) break;
            var oosEnd = oosStart.AddYears(OosYears).AddDays(-1);
            folds.Add(new Fold(folds.Count, y, valStart.AddDays(-1), valStart, oosStart.AddDays(-1), oosStart, oosEnd > LastDate ? LastDate : oosEnd));
        }
        return folds;
    }
}

public sealed record FoldOutcome(Fold Fold, string? SelectedParameters, double TrainSharpe, double ValidationEv, bool Traded, BacktestResult? Oos, BacktestResult RandomOos);

public sealed record StudyOutcome(ResearchStudy Study, IReadOnlyList<FoldOutcome> Folds, OosEvidence Evidence, GateResult Gate, Guid EvaluatedVersionId);

public sealed class WalkForwardRunner(BacktestRunner runner, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    /// <param name="diagnostic">
    /// Robustness diagnostics (parameter neighborhoods, cost stress): the gate is evaluated and stored, but
    /// strategy statuses never change — a sweep of neighbors must not promote anything (multiple testing).
    /// </param>
    public async Task<StudyOutcome> RunAsync(string strategyId, string hypothesis, WalkForwardPlan plan, UniverseDefinition universe,
        Func<DateOnly, DateOnly, BacktestConfig> config, GateCriteria criteria, CancellationToken ct = default, bool diagnostic = false,
        string? fingerprint = null, bool postHoc = false)
    {
        var code = CodeVersion.Detect();
        // multiple testing: every earlier (non-diagnostic) study of this strategy id counts as variants tried
        int priorStudies;
        await using (var db0 = runner.DbFactory())
        {
            // the whole family (e.g. meanrev.*) counts: related ideas share the same data-mining budget
            var familyPrefix = strategyId.Split('.')[0] + ".";
            priorStudies = await db0.ResearchStudies.CountAsync(s => (s.StrategyId == strategyId || s.StrategyId.StartsWith(familyPrefix)) && s.Kind == "walk-forward", ct);
        }
        var variants = plan.ParameterGrid.Count + (diagnostic ? 0 : priorStudies);
        var study = new ResearchStudy
        {
            Id = Guid.NewGuid(), StrategyId = strategyId, Kind = diagnostic ? "robustness" : "walk-forward", Hypothesis = hypothesis,
            PlanJson = JsonSerializer.Serialize(new { plan, universe, criteria, sample = config(plan.FirstTrainStart, plan.LastDate), fingerprint, priorStudies }),
            VariantsTried = variants, CodeCommit = code.Commit, CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var db = runner.DbFactory()) { db.ResearchStudies.Add(study); await db.SaveChangesAsync(ct); }

        var usesEvents = plan.ParameterGrid.Any(p => StrategyCatalog.Create(strategyId, p).UsesCorporateEvents);
        var usesFundamentals = plan.ParameterGrid.Any(p => StrategyCatalog.Create(strategyId, p).UsesFundamentals);
        var data = await runner.LoadDataAsync(universe, plan.FirstTrainStart, plan.LastDate, ct, usesEvents, usesFundamentals);
        var folds = new List<FoldOutcome>();
        Guid? lastSelectedVersion = null;
        foreach (var f in plan.Folds())
        {
            // 1) train: choose parameters by net Sharpe on the train window only
            (string? Params, double Sharpe, Guid VersionId)? best = null;
            foreach (var p in plan.ParameterGrid)
            {
                var o = await runner.RunOnDataAsync(StrategyCatalog.Create(strategyId, p), data,
                    new BacktestRequest(strategyId, p, universe, config(f.TrainStart, f.TrainEnd), "wf-train", $"fold {f.Index}", StudyId: study.Id), ct);
                if (best is null || o.Net.Sharpe > best.Value.Sharpe) best = (p, o.Net.Sharpe, o.Run.StrategyVersionId);
            }
            // 2) validation: the chosen parameters must show positive net EV before any OOS trading
            var val = await runner.RunOnDataAsync(StrategyCatalog.Create(strategyId, best!.Value.Params), data,
                new BacktestRequest(strategyId, best.Value.Params, universe, config(f.ValidationStart, f.ValidationEnd), "wf-validation", $"fold {f.Index}", StudyId: study.Id), ct);
            var traded = val.Net.NumberOfTrades > 0 && val.Net.ExpectedValuePerTrade > 0;
            lastSelectedVersion = best.Value.VersionId;

            // 3) out-of-sample: run once; untraded folds are recorded but count as flat in the evidence
            var oos = await runner.RunOnDataAsync(StrategyCatalog.Create(strategyId, best.Value.Params), data,
                new BacktestRequest(strategyId, best.Value.Params, universe, config(f.OosStart, f.OosEnd), traded ? "wf-oos" : "wf-oos-untraded", $"fold {f.Index}", StudyId: study.Id), ct);
            var random = await runner.RunOnDataAsync(new RandomEntryStrategy(), data,
                new BacktestRequest(RandomEntryStrategy.Id, null, universe, config(f.OosStart, f.OosEnd), "wf-oos-control", $"fold {f.Index}", StudyId: study.Id), ct);
            folds.Add(new FoldOutcome(f, best.Value.Params, best.Value.Sharpe, val.Net.ExpectedValuePerTrade, traded, traded ? oos.Result : null, random.Result));
            _log($"fold {f.Index}: train {f.TrainStart:yyyy}-{f.TrainEnd:yyyy} sharpe {best.Value.Sharpe:F2} | val EV {val.Net.ExpectedValuePerTrade:P3} -> {(traded ? "TRADE" : "flat")} | OOS {f.OosStart:yyyy-MM}..{f.OosEnd:yyyy-MM} net {oos.Net.TotalReturn:P1} EV {oos.Net.ExpectedValuePerTrade:P3}");
        }

        var evidence = Aggregate(folds, data, plan) with { VariantsTried = variants };
        StrategyVersion version;
        await using (var db = runner.DbFactory())
            version = await db.StrategyVersions.Include(v => v.Strategy).SingleAsync(v => v.Id == lastSelectedVersion, ct);
        var gate = PromotionGate.EvaluateValidation(version.Status, evidence, criteria);
        if (postHoc) gate = PromotionGate.CapPostHoc(gate, version.Status);
        await RecordAsync(study, version, evidence, criteria, gate, hypothesis, plan, diagnostic, ct);
        return new StudyOutcome(study, folds, evidence, gate, version.Id);
    }

    private async Task RecordAsync(ResearchStudy study, StrategyVersion version, OosEvidence evidence, GateCriteria criteria, GateResult gate,
        string hypothesis, WalkForwardPlan plan, bool diagnostic, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var evaluation = new StrategyEvaluation
        {
            Id = Guid.NewGuid(), StrategyVersionId = version.Id, StudyId = study.Id, Stage = diagnostic ? "robustness" : "walk-forward-oos",
            FromStatus = version.Status, ToStatus = diagnostic ? version.Status : gate.To, Decision = gate.Decision,
            EvidenceJson = JsonSerializer.Serialize(evidence), CriteriaJson = JsonSerializer.Serialize(criteria),
            Reasons = string.Join("; ", gate.Reasons), EvaluatedAt = now,
        };
        await using var db = runner.DbFactory();
        db.StrategyEvaluations.Add(evaluation);
        var v = await db.StrategyVersions.Include(x => x.Strategy).SingleAsync(x => x.Id == version.Id, ct);
        if (!diagnostic) v.Status = gate.To;
        if (gate.Decision == GateDecision.Reject && !diagnostic)
        {
            var worst = evidence.ByRegime.Where(r => r.Value.Trades > 0).OrderBy(r => r.Value.NetEvPerTrade).Select(r => $"{r.Key}: EV {r.Value.NetEvPerTrade:P2} ({r.Value.Trades} trades)");
            db.ExperimentFailures.Add(new ExperimentFailure
            {
                Id = Guid.NewGuid(), StrategyId = v.StrategyId, StrategyVersionId = v.Id, EvaluationId = evaluation.Id,
                Hypothesis = hypothesis, ParametersJson = JsonSerializer.Serialize(plan.ParameterGrid),
                TestFrom = evidence.From, TestTo = evidence.To, ResultJson = evaluation.EvidenceJson,
                FailureReason = evaluation.Reasons, RegimeNotes = string.Join("; ", worst), RejectedAt = now,
            });
        }
        var s = await db.ResearchStudies.SingleAsync(x => x.Id == study.Id, ct);
        s.SummaryJson = evaluation.EvidenceJson;
        s.CompletedAt = now;
        await db.SaveChangesAsync(ct);
        await db.RollupStrategyStatusAsync(v.StrategyId, ct);
    }

    /// <summary>Chains OOS folds into one track record (untraded folds are flat) and computes net evidence.</summary>
    public static OosEvidence Aggregate(IReadOnlyList<FoldOutcome> folds, MarketDataSet data, WalkForwardPlan plan)
    {
        var capital = 100_000_000m;
        var trades = new List<TradeRecord>();
        var equity = new List<EquityPoint>();
        var level = capital;
        foreach (var f in folds)
        {
            if (f.Oos is { } r)
            {
                var scale = level / r.Config.InitialCapital;
                foreach (var e in r.Equity)
                    equity.Add(e with { NetEquity = e.NetEquity * scale, GrossEquity = e.GrossEquity * scale, Cash = e.Cash * scale, InvestedValue = e.InvestedValue * scale });
                trades.AddRange(r.Trades);
                level = equity[^1].NetEquity;
            }
            else
            {
                foreach (var e in f.RandomOos.Equity) // same session dates, flat equity
                    equity.Add(new EquityPoint(e.Date, level, level, level, 0, 0));
            }
        }
        var first = folds[0];
        var combined = new BacktestResult
        {
            Strategy = first.RandomOos.Strategy, Config = first.RandomOos.Config with { InitialCapital = capital },
            Trades = trades, Equity = equity, RiskEvents = [], Rejections = new Dictionary<string, int>(),
            DataHash = "", UniverseHash = "", DataSource = "", SignalCount = 0,
            BenchmarkReturn = IndexReturn(data, folds[0].Fold.OosStart, folds[^1].Fold.OosEnd),
        };
        var net = Metrics.Compute(combined, CostBasis.Net);
        var gross = Metrics.Compute(combined, CostBasis.Gross);
        var randomTrades = folds.SelectMany(f => f.RandomOos.Trades).ToList();

        return new OosEvidence
        {
            Folds = folds.Count,
            FoldsTraded = folds.Count(f => f.Traded),
            FoldsPositive = folds.Count(f => f.Oos is { } r && r.Equity.Count > 0 && r.Equity[^1].NetEquity > r.Config.InitialCapital),
            Trades = trades.Count,
            NetEvPerTrade = net.ExpectedValuePerTrade,
            NetEvTStat = net.ExpectedValueTStat,
            GrossEvPerTrade = gross.ExpectedValuePerTrade,
            NetSharpe = net.Sharpe,
            NetMaxDrawdown = net.MaxDrawdown,
            NetAvgDailyReturn = net.AvgDailyReturn,
            NetTotalReturn = net.TotalReturn,
            NetCagr = net.Cagr,
            RandomControlNetEvPerTrade = randomTrades.Count == 0 ? 0 : randomTrades.Average(t => t.NetReturn),
            BenchmarkReturn = combined.BenchmarkReturn,
            VariantsTried = plan.ParameterGrid.Count,
            From = folds[0].Fold.OosStart,
            To = folds[^1].Fold.OosEnd,
            ByRegime = RegimeBreakdown(trades, equity, capital, data),
        };
    }

    /// <summary>Trades grouped by the regime known at the entry signal; sessions by the regime known at the previous close.</summary>
    public static IReadOnlyDictionary<string, RegimeStats> RegimeBreakdown(IReadOnlyList<TradeRecord> trades, IReadOnlyList<EquityPoint> equity, decimal initial, MarketDataSet data)
    {
        var index = data.Index;
        var labels = new Dictionary<DateOnly, string>();
        for (var i = 0; i < index.Length; i++)
        {
            var label = RegimeClassifier.Classify(new BarSeries(data.IndexCode, index, i + 1));
            labels[index[i].Date] = label?.ToString() ?? "Unknown";
        }
        string At(DateOnly d) => labels.TryGetValue(d, out var l) ? l : "Unknown";

        var byTrade = trades.GroupBy(t => At(t.EntrySignalDate)).ToDictionary(g => g.Key, g => g.Select(t => t.NetReturn).ToList());
        var daily = new Dictionary<string, List<double>>();
        var prevEq = (double)initial;
        DateOnly? prevDate = null;
        foreach (var e in equity)
        {
            var r = (double)e.NetEquity / prevEq - 1;
            prevEq = (double)e.NetEquity;
            var key = prevDate is { } pd ? At(pd) : "Unknown";
            prevDate = e.Date;
            if (!daily.TryGetValue(key, out var list)) daily[key] = list = [];
            list.Add(r);
        }
        return byTrade.Keys.Union(daily.Keys).Order().ToDictionary(k => k, k =>
        {
            var tr = byTrade.GetValueOrDefault(k) ?? [];
            var dr = daily.GetValueOrDefault(k) ?? [];
            var sd = Metrics.Stdev(tr);
            return new RegimeStats(tr.Count, tr.Count == 0 ? 0 : tr.Average(), tr.Count > 1 && sd > 0 ? tr.Average() / (sd / Math.Sqrt(tr.Count)) : 0,
                dr.Count, dr.Count == 0 ? 0 : dr.Average());
        });
    }

    private static double IndexReturn(MarketDataSet data, DateOnly from, DateOnly to)
    {
        var before = data.Index.LastOrDefault(b => b.Date < from);
        var last = data.Index.LastOrDefault(b => b.Date <= to);
        return before.Close > 0 && last.Close > 0 ? last.Close / before.Close - 1 : double.NaN;
    }
}
