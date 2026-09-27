using Investment.Backtest;
using Investment.Domain.Research;
using Investment.MarketData.Universe;
using Investment.Persistence;
using Investment.Research;
using Investment.Risk;
using Investment.Strategies;
using Microsoft.EntityFrameworkCore;

namespace Investment.Cli;

public static class ResearchCommands
{
    public static UniverseDefinition Universe(CliOptions o) => new()
    {
        TopN = o.GetInt("top", 100),
        LiquidityLookback = o.GetInt("liquidity-lookback", 60),
        MinHistoryBars = o.GetInt("min-history", 120),
        MinPrice = o.GetDouble("min-price", 1000),
        Tickers = o.Get("tickers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    public static BacktestConfig Config(CliOptions o, DateOnly start, DateOnly end) => new()
    {
        Start = start,
        End = end,
        InitialCapital = (decimal)o.GetDouble("capital", 100_000_000),
        Costs = new CostModel
        {
            CommissionRate = o.GetDouble("commission", 0.00015),
            BaseSlippage = o.GetDouble("slippage", 0.001),
            ImpactCoefficient = o.GetDouble("impact", 0.1),
        },
        Risk = new RiskLimits
        {
            MaxPositions = o.GetInt("max-positions", 10),
            MaxPositionWeight = o.GetDouble("max-weight", 0.10),
            MaxSectorWeight = o.GetDouble("max-sector", 0.30),
            MaxDailyLoss = o.GetDouble("max-daily-loss", 0.03),
            MaxDrawdown = o.GetDouble("max-drawdown", 0.25),
            StopLoss = o.Get("stop-loss") is "none" ? null : o.GetDouble("stop-loss", 0.10),
            MaxParticipation = o.GetDouble("max-participation", 0.05),
        },
    };

    public static async Task<int> BacktestAsync(CliOptions o, CancellationToken ct)
    {
        var cs = Database.ConnectionString(o.Get("db"));
        var ids = o.Require("strategy") is "all" ? StrategyCatalog.Ids.ToList() : o.Require("strategy").Split(',').ToList();
        var start = o.GetDate("from", new DateOnly(2017, 1, 1));
        var end = o.GetDate("to", DateOnly.FromDateTime(DateTime.Today));
        var runner = new BacktestRunner(cs, Log);
        foreach (var id in ids)
        {
            var outcome = await runner.RunAsync(new BacktestRequest(id, o.Get("params"), Universe(o), Config(o, start, end), Label: o.Get("label")), ct);
            Console.WriteLine(ReportFormatter.Format(outcome));
        }
        return 0;
    }

    public static async Task<int> RerunAsync(CliOptions o, CancellationToken ct)
    {
        var runner = new BacktestRunner(Database.ConnectionString(o.Get("db")), Log);
        var report = await runner.RerunAsync(Guid.Parse(o.Require("run")), ct);
        Console.WriteLine($"run {report.RunId}");
        Console.WriteLine($"  data   stored {report.StoredDataHash[..16]} now {report.NewDataHash[..16]} -> {(report.DataMatches ? "MATCH" : "DIFFERENT (source data changed)")}");
        Console.WriteLine($"  params {(report.ParametersMatch ? "MATCH" : "DIFFERENT")}");
        Console.WriteLine($"  result stored {report.StoredResultHash[..16]} now {report.NewResultHash[..16]} -> {(report.ResultMatches ? "REPRODUCED" : "NOT REPRODUCED")}");
        if (report.FirstDifference is not null) Console.WriteLine($"  first difference: {report.FirstDifference}");
        return report.ResultMatches ? 0 : 1;
    }

    public static async Task<int> ListRunsAsync(CliOptions o, CancellationToken ct)
    {
        await using var db = Database.Create(o.Get("db"));
        var runs = await db.BacktestRuns.AsNoTracking().Include(r => r.StrategyVersion).Include(r => r.Metrics)
            .OrderByDescending(r => r.CreatedAt).Take(o.GetInt("limit", 20)).ToListAsync(ct);
        Console.WriteLine($"{"run",-36}  {"strategy",-16} {"v",2} {"kind",-14} {"period",-21} {"net ret",9} {"avg/day",8} {"MDD",7} {"sharpe",6} {"trades",6}");
        foreach (var r in runs)
        {
            var n = r.Metrics.Single(m => m.Basis == CostBasis.Net);
            Console.WriteLine($"{r.Id}  {r.StrategyVersion!.StrategyId,-16} {r.StrategyVersion.Version,2} {r.RunKind,-14} {r.StartDate:yyyy-MM-dd}..{r.EndDate:yyyy-MM-dd} " +
                              $"{ReportFormatter.P(n.TotalReturn),9} {ReportFormatter.P(n.AvgDailyReturn, 3),8} {ReportFormatter.P(n.MaxDrawdown),7} {n.Sharpe,6:F2} {n.NumberOfTrades,6}");
        }
        return 0;
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
