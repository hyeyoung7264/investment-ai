using System.Text;
using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Research;
using Investment.MarketData.Universe;
using Investment.Strategies;
using Microsoft.EntityFrameworkCore;

namespace Investment.Research.Agent;

/// <summary>A candidate study proposed by a generator. It is only a hypothesis until the gate says otherwise.</summary>
public sealed record HypothesisProposal(
    string StrategyId,
    IReadOnlyList<string?> ParameterGrid,
    string Hypothesis,
    string Rationale,
    string Origin,
    int? UniverseTopN = null,
    IReadOnlyList<string>? Markets = null);

/// <summary>What the agent knows at the start of a cycle (all from stored evidence).</summary>
public sealed record ResearchContext(
    IReadOnlyList<StrategyDefinition> Strategies,
    IReadOnlyList<(ResearchStudy Study, StrategyEvaluation? Evaluation)> Studies,
    IReadOnlyList<ExperimentFailure> Failures,
    IReadOnlyCollection<string> CatalogIds,
    string? CurrentRegime);

public interface IHypothesisGenerator
{
    string Name { get; }
    Task<IReadOnlyList<HypothesisProposal>> ProposeAsync(ResearchContext context, CancellationToken ct);
}

public sealed record AgentCycleResult(
    DateTimeOffset StartedAt,
    IReadOnlyList<HypothesisProposal> Proposed,
    IReadOnlyList<(HypothesisProposal Proposal, string Reason)> Skipped,
    IReadOnlyList<(HypothesisProposal Proposal, StudyOutcome Outcome)> Executed,
    string ReportPath);

/// <summary>
/// Research loop: observe stored evidence → propose hypotheses → drop duplicates of past studies → run walk-forward
/// studies through the same Promotion Gate → write a report. The agent cannot promote anything by itself; it can
/// only create evidence. Live trading is out of scope.
/// </summary>
public sealed class ResearchAgent(BacktestRunner runner, IReadOnlyList<IHypothesisGenerator> generators, Action<string>? log = null)
{
    private readonly Action<string> _log = log ?? (_ => { });

    public static string Fingerprint(HypothesisProposal p, WalkForwardPlan plan) =>
        BacktestRecorder.Sha256(JsonSerializer.Serialize(new
        {
            p.StrategyId,
            Grid = p.ParameterGrid.Select(Canonical).ToList(),
            p.UniverseTopN,
            Markets = p.Markets ?? [],
            plan.TrainYears, plan.ValidationYears, plan.OosYears, plan.StepYears, plan.FirstTrainStart,
        }));

    private static string Canonical(string? json)
    {
        if (json is null) return "default";
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        return Sort(node)!.ToJsonString();

        static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? n) => n switch
        {
            System.Text.Json.Nodes.JsonObject o => new System.Text.Json.Nodes.JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
            System.Text.Json.Nodes.JsonArray a => new System.Text.Json.Nodes.JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
            _ => n?.DeepClone(),
        };
    }

    public async Task<ResearchContext> ObserveAsync(CancellationToken ct)
    {
        await using var db = runner.DbFactory();
        var strategies = await db.Strategies.AsNoTracking().ToListAsync(ct);
        var studies = await db.ResearchStudies.AsNoTracking().Where(s => s.Kind == "walk-forward").OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var evals = await db.StrategyEvaluations.AsNoTracking().Where(e => e.StudyId != null).ToListAsync(ct);
        var failures = await db.ExperimentFailures.AsNoTracking().ToListAsync(ct);
        var regime = await db.MarketRegimes.AsNoTracking().OrderByDescending(r => r.Date).FirstOrDefaultAsync(ct);
        return new ResearchContext(strategies,
            studies.Select(s => (s, evals.Where(e => e.StudyId == s.Id).OrderByDescending(e => e.EvaluatedAt).FirstOrDefault())).ToList(),
            failures, StrategyCatalog.Ids, regime is null ? null : $"{regime.Trend}/{regime.Volatility}Vol");
    }

    public async Task<AgentCycleResult> RunCycleAsync(WalkForwardPlan plan, UniverseDefinition baseUniverse,
        Func<DateOnly, DateOnly, BacktestConfig> config, int maxStudies, bool dryRun, string reportDir, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var context = await ObserveAsync(ct);
        var proposals = new List<HypothesisProposal>();
        foreach (var g in generators)
        {
            try { proposals.AddRange(await g.ProposeAsync(context, ct)); }
            catch (Exception e) when (e is not OperationCanceledException) { _log($"generator {g.Name} failed: {e.Message}"); }
        }

        var seen = new HashSet<string>();
        foreach (var (study, _) in context.Studies)
            if (TryFingerprintFromPlan(study.PlanJson, study.StrategyId) is { } fp) seen.Add(fp);

        var skipped = new List<(HypothesisProposal, string)>();
        var queue = new List<HypothesisProposal>();
        foreach (var p in proposals)
        {
            if (!context.CatalogIds.Contains(p.StrategyId, StringComparer.OrdinalIgnoreCase)) { skipped.Add((p, "unknown strategy id")); continue; }
            var fp = Fingerprint(p, plan);
            if (!seen.Add(fp)) { skipped.Add((p, "already studied (same strategy, parameters, universe, plan)")); continue; }
            try { foreach (var g in p.ParameterGrid) StrategyCatalog.Create(p.StrategyId, g); }
            catch (Exception e) { skipped.Add((p, $"invalid parameters: {e.Message}")); continue; }
            if (queue.Count >= maxStudies) { skipped.Add((p, "cycle budget exhausted")); continue; }
            queue.Add(p);
        }

        var executed = new List<(HypothesisProposal, StudyOutcome)>();
        if (!dryRun)
        {
            var wf = new WalkForwardRunner(runner, _log);
            foreach (var p in queue)
            {
                var universe = baseUniverse with
                {
                    TopN = p.UniverseTopN ?? baseUniverse.TopN,
                    Markets = p.Markets is { Count: > 0 } m ? m.Select(Enum.Parse<Investment.Domain.Market.MarketType>).ToList() : baseUniverse.Markets,
                };
                _log($"agent: studying {p.StrategyId} — {p.Hypothesis}");
                var outcome = await wf.RunAsync(p.StrategyId, $"[{p.Origin}] {p.Hypothesis}", plan with { ParameterGrid = p.ParameterGrid },
                    universe, config, new GateCriteria(), ct, fingerprint: Fingerprint(p, plan));
                executed.Add((p, outcome));
            }
        }

        var report = await WriteReportAsync(started, context, proposals, skipped, executed, dryRun ? queue : [], dryRun, reportDir, ct);
        return new AgentCycleResult(started, proposals, skipped, executed, report);
    }

    /// <summary>Stored fingerprint, or one reconstructed from the stored plan/universe for studies that predate fingerprints.</summary>
    public static string? TryFingerprintFromPlan(string planJson, string? strategyId = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String) return f.GetString();
            if (strategyId is null || !root.TryGetProperty("plan", out var planEl) || !root.TryGetProperty("universe", out var uniEl)) return null;
            var plan = planEl.Deserialize<WalkForwardPlan>()!;
            var universe = uniEl.Deserialize<UniverseDefinition>()!;
            var markets = universe.Markets.Count == 2 ? null : universe.Markets.Select(m => m.ToString()).ToList();
            return Fingerprint(new HypothesisProposal(strategyId, plan.ParameterGrid, "", "", "", universe.TopN == 100 ? null : universe.TopN, markets), plan);
        }
        catch (JsonException) { return null; }
    }

    private async Task<string> WriteReportAsync(DateTimeOffset started, ResearchContext ctx, List<HypothesisProposal> proposals,
        List<(HypothesisProposal, string)> skipped, List<(HypothesisProposal, StudyOutcome)> executed, List<HypothesisProposal> queued,
        bool dryRun, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"cycle-{started:yyyyMMdd-HHmmss}.md");
        var sb = new StringBuilder();
        sb.AppendLine($"# Research cycle {started:yyyy-MM-dd HH:mm} UTC{(dryRun ? " (dry run)" : "")}");
        sb.AppendLine();
        sb.AppendLine($"Current market regime: **{ctx.CurrentRegime ?? "unknown"}**. Prior walk-forward studies: {ctx.Studies.Count}, recorded failures: {ctx.Failures.Count}.");
        sb.AppendLine();
        sb.AppendLine("## Executed studies");
        if (executed.Count == 0) sb.AppendLine("- none");
        foreach (var (p, o) in executed)
        {
            var e = o.Evidence;
            sb.AppendLine($"- **{p.StrategyId}** [{p.Origin}] {p.Hypothesis}");
            sb.AppendLine($"  - rationale: {p.Rationale}");
            sb.AppendLine($"  - OOS {e.From:yyyy-MM}..{e.To:yyyy-MM}: {e.Trades} trades, EV/trade {ReportFormatter.P(e.NetEvPerTrade, 3)} (t={e.NetEvTStat:F2}, required {Stats.RequiredT(e.VariantsTried):F2} for {e.VariantsTried} variants), Sharpe {e.NetSharpe:F2}, MDD {ReportFormatter.P(e.NetMaxDrawdown)}, total {ReportFormatter.P(e.NetTotalReturn)}");
            sb.AppendLine($"  - gate: **{o.Gate.Decision} → {o.Gate.To}** — {string.Join("; ", o.Gate.Reasons)}");
        }
        sb.AppendLine();
        if (dryRun)
        {
            sb.AppendLine("## Would run (dry run)");
            foreach (var p in queued) sb.AppendLine($"- **{p.StrategyId}** [{p.Origin}] {p.Hypothesis} — {string.Join(" | ", p.ParameterGrid.Select(g => g ?? "defaults"))}\n  - rationale: {p.Rationale}");
            if (queued.Count == 0) sb.AppendLine("- none");
            sb.AppendLine();
        }
        sb.AppendLine("## Proposals not executed");
        foreach (var (p, why) in skipped) sb.AppendLine($"- {p.StrategyId} {string.Join(" | ", p.ParameterGrid.Select(g => g ?? "defaults"))} — {why}");
        if (skipped.Count == 0) sb.AppendLine("- none");
        sb.AppendLine();
        sb.AppendLine("## Strategy statuses");
        await using (var db = runner.DbFactory())
            foreach (var s in await db.Strategies.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
                sb.AppendLine($"- `{s.Id}`: {s.Status}");
        sb.AppendLine();
        sb.AppendLine("_Backtest/OOS evidence is not paper evidence; nothing here authorizes live trading._");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return path;
    }
}
