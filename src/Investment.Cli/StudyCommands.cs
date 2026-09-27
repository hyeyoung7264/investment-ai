using System.Text.Json;
using Investment.Backtest;
using Investment.Domain.Market;
using Investment.Persistence;
using Investment.Research;
using Microsoft.EntityFrameworkCore;

namespace Investment.Cli;

public static class StudyCommands
{
    public static async Task<int> WalkForwardAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var strategyId = o.Require("strategy");
        var grid = o.Get("grid") is { } g
            ? JsonSerializer.Deserialize<List<JsonElement>>(g)!.Select(e => (string?)e.GetRawText()).ToList()
            : [o.Get("params")];
        var plan = new WalkForwardPlan
        {
            FirstTrainStart = o.GetDate("from", new DateOnly(2017, 1, 1)),
            LastDate = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today)),
            TrainYears = o.GetInt("train", 3),
            ValidationYears = o.GetInt("validation", 1),
            OosYears = o.GetInt("oos", 1),
            StepYears = o.GetInt("step", 1),
            ParameterGrid = grid,
        };
        var universe = ResearchCommands.Universe(o);
        if (o.Get("markets") is { } m)
            universe = universe with { Markets = m.Split(',').Select(x => Enum.Parse<MarketType>(x, true)).ToList() };
        var runner = new BacktestRunner(cs, Log);
        var outcome = await new WalkForwardRunner(runner, Log).RunAsync(strategyId, o.Require("hypothesis"), plan, universe,
            (s, e) => ResearchCommands.Config(o, s, e), new GateCriteria(), ct, diagnostic: o.Has("diagnostic"));

        var e2 = outcome.Evidence;
        Console.WriteLine($"""

            == walk-forward study {outcome.Study.Id} — {strategyId}
               hypothesis: {outcome.Study.Hypothesis}
               OOS {e2.From:yyyy-MM-dd}..{e2.To:yyyy-MM-dd}, folds {e2.Folds} (traded {e2.FoldsTraded}, positive {e2.FoldsPositive}), variants tried {e2.VariantsTried}
               OOS net: total {ReportFormatter.P(e2.NetTotalReturn)}  CAGR {ReportFormatter.P(e2.NetCagr)}  avg/day {ReportFormatter.P(e2.NetAvgDailyReturn, 3)}  Sharpe {e2.NetSharpe:F2}  MDD {ReportFormatter.P(e2.NetMaxDrawdown)}
               OOS trades {e2.Trades}: EV/trade net {ReportFormatter.P(e2.NetEvPerTrade, 3)} (t={e2.NetEvTStat:F2}, required {Stats.RequiredT(e2.VariantsTried):F2}), gross {ReportFormatter.P(e2.GrossEvPerTrade, 3)}
               random-entry control EV/trade net {ReportFormatter.P(e2.RandomControlNetEvPerTrade, 3)}   benchmark KOSPI {ReportFormatter.P(e2.BenchmarkReturn)}
               by regime (entry): {string.Join("  ", e2.ByRegime.Where(r => r.Value.Trades > 0).Select(r => $"{r.Key}: {r.Value.Trades}tr EV {ReportFormatter.P(r.Value.NetEvPerTrade, 2)} t={r.Value.NetEvTStat:F1}"))}
               GATE: {outcome.Gate.Decision} -> {outcome.Gate.To}
                 {string.Join("\n     ", outcome.Gate.Reasons)}
            """);
        return 0;
    }

    /// <summary>
    /// Overfitting diagnostics for one parameter set: parameter neighborhood, cost stress and universe size.
    /// Each variation is a separate diagnostic walk-forward (no status changes). A real edge should survive
    /// most neighbors; an edge that exists only at the chosen point is likely fitted noise.
    /// </summary>
    public static async Task<int> RobustnessAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var strategyId = o.Require("strategy");
        var baseParams = System.Text.Json.Nodes.JsonNode.Parse(o.Require("params"))!.AsObject();
        var neighbors = o.Get("neighbors") is { } n
            ? JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(n)!
            : [];
        var runner = new BacktestRunner(cs, _ => { });
        var wf = new WalkForwardRunner(runner, _ => { });
        var plan = new WalkForwardPlan { FirstTrainStart = o.GetDate("from", new DateOnly(2017, 1, 1)), LastDate = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today)) };
        var baseUniverse = ResearchCommands.Universe(o);

        var cases = new List<(string Name, string Params, Investment.MarketData.Universe.UniverseDefinition Universe, double SlippageMultiplier)>
        {
            ("base", baseParams.ToJsonString(), baseUniverse, 1),
        };
        foreach (var change in neighbors)
        {
            var p = baseParams.DeepClone().AsObject();
            foreach (var (k, v) in change) p[k] = System.Text.Json.Nodes.JsonNode.Parse(v.GetRawText());
            cases.Add(($"params {JsonSerializer.Serialize(change)}", p.ToJsonString(), baseUniverse, 1));
        }
        foreach (var m in (o.Get("cost-stress") ?? "2").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)))
            cases.Add(($"slippage x{m}", baseParams.ToJsonString(), baseUniverse, m));
        foreach (var top in (o.Get("universe-sizes") ?? "50,200").Split(',').Select(int.Parse))
            cases.Add(($"universe top {top}", baseParams.ToJsonString(), baseUniverse with { TopN = top }, 1));

        Console.WriteLine($"{"case",-44} {"trades",6} {"EV/trade",9} {"t",6} {"Sharpe",6} {"MDD",7} {"total",8} {"folds+",6} gate");
        foreach (var c in cases)
        {
            BacktestConfig Cfg(DateOnly s, DateOnly e)
            {
                var cfg = ResearchCommands.Config(o, s, e);
                return cfg with { Costs = cfg.Costs with { BaseSlippage = cfg.Costs.BaseSlippage * c.SlippageMultiplier } };
            }
            var outcome = await wf.RunAsync(strategyId, $"robustness of {baseParams.ToJsonString()}: {c.Name}", plan with { ParameterGrid = [c.Params] },
                c.Universe, Cfg, new GateCriteria(), ct, diagnostic: true);
            var e = outcome.Evidence;
            Console.WriteLine($"{c.Name,-44} {e.Trades,6} {ReportFormatter.P(e.NetEvPerTrade, 3),9} {e.NetEvTStat,6:F2} {e.NetSharpe,6:F2} {ReportFormatter.P(e.NetMaxDrawdown),7} {ReportFormatter.P(e.NetTotalReturn),8} {e.FoldsPositive,3}/{e.Folds,-2} {outcome.Gate.Decision}");
        }
        return 0;
    }

    public static async Task<int> RegimeAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var code = o.Get("index") ?? "KOSPI";
        var bars = (await new MarketDataStore(cs).LoadIndexAsync(code, new DateOnly(2000, 1, 1), DateOnly.FromDateTime(DateTime.Today), ct)).ToArray();
        var rows = new List<MarketRegimeDay>();
        for (var i = 0; i < bars.Length; i++)
        {
            var c = RegimeClassifier.Compute(new BarSeries(code, bars, i + 1));
            if (c is null) continue;
            rows.Add(new MarketRegimeDay
            {
                IndexCode = code, Date = bars[i].Date, Trend = c.Value.Label.Trend, Volatility = c.Value.Label.Volatility,
                Close = bars[i].Close, Sma200 = c.Value.Sma, RealizedVol20 = c.Value.Vol, VolThreshold = c.Value.Threshold,
            });
        }
        await using (var db = Database.Create(cs))
        {
            await db.MarketRegimes.Where(r => r.IndexCode == code).ExecuteDeleteAsync(ct);
            db.MarketRegimes.AddRange(rows);
            await db.SaveChangesAsync(ct);
        }
        Console.WriteLine($"{code}: {rows.Count} regime days stored");
        foreach (var y in rows.GroupBy(r => r.Date.Year))
            Console.WriteLine($"  {y.Key}: " + string.Join("  ", y.GroupBy(r => $"{r.Trend}/{r.Volatility}").OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}")));
        var last = rows[^1];
        Console.WriteLine($"latest {last.Date:yyyy-MM-dd}: {last.Trend}/{last.Volatility}Vol close {last.Close:F2} SMA200 {last.Sma200:F2} vol20 {last.RealizedVol20:P1} (high > {last.VolThreshold:P1})");
        return 0;
    }

    public static async Task<int> EvaluationsAsync(CliOptions o, CancellationToken ct)
    {
        await using var db = Database.Create(o.Get("db"));
        var evals = await db.StrategyEvaluations.AsNoTracking().OrderByDescending(e => e.EvaluatedAt).Take(o.GetInt("limit", 30))
            .Join(db.StrategyVersions, e => e.StrategyVersionId, v => v.Id, (e, v) => new { e, v }).ToListAsync(ct);
        foreach (var x in evals)
            Console.WriteLine($"{x.e.EvaluatedAt:yyyy-MM-dd HH:mm} {x.v.StrategyId} v{x.v.Version} [{x.e.Stage}] {x.e.FromStatus} -> {x.e.ToStatus} ({x.e.Decision}): {x.e.Reasons}");
        Console.WriteLine();
        foreach (var s in await db.Strategies.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
            Console.WriteLine($"  {s.Id,-28} {s.Status}");
        return 0;
    }

    public static async Task<int> FailuresAsync(CliOptions o, CancellationToken ct)
    {
        await using var db = Database.Create(o.Get("db"));
        foreach (var f in await db.ExperimentFailures.AsNoTracking().OrderByDescending(f => f.RejectedAt).ToListAsync(ct))
            Console.WriteLine($"{f.RejectedAt:yyyy-MM-dd} {f.StrategyId} [{f.TestFrom:yyyy-MM}..{f.TestTo:yyyy-MM}] {f.FailureReason}\n    hypothesis: {f.Hypothesis}\n    regimes: {f.RegimeNotes}");
        return 0;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}

public static class AgentCommands
{
    public static async Task<int> CycleAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var runner = new BacktestRunner(cs, Log);
        var generators = new List<Investment.Research.Agent.IHypothesisGenerator> { new Investment.Research.Agent.RuleBasedGenerator() };
        if (Investment.Research.Agent.LlmHypothesisGenerator.FromEnvironment() is { } llm) generators.Add(llm);
        else Log("LLM generator disabled (ANTHROPIC_API_KEY not set); using rule-based proposals only");
        var agent = new Investment.Research.Agent.ResearchAgent(runner, generators, Log);
        var plan = new WalkForwardPlan { FirstTrainStart = o.GetDate("from", new DateOnly(2017, 1, 1)), LastDate = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today)) };
        var result = await agent.RunCycleAsync(plan, ResearchCommands.Universe(o), (s, e) => ResearchCommands.Config(o, s, e),
            o.GetInt("max-studies", 3), o.Has("dry-run"), Path.Combine("reports", "research"), ct);
        Console.WriteLine($"proposed {result.Proposed.Count}, executed {result.Executed.Count}, skipped {result.Skipped.Count}");
        foreach (var (p, outcome) in result.Executed)
            Console.WriteLine($"  {p.StrategyId} [{p.Origin}] -> {outcome.Gate.Decision}: {string.Join("; ", outcome.Gate.Reasons)}");
        foreach (var (p, why) in result.Skipped)
            Console.WriteLine($"  skipped {p.StrategyId} [{p.Origin}]: {why}");
        Console.WriteLine($"report: {result.ReportPath}");
        return 0;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
