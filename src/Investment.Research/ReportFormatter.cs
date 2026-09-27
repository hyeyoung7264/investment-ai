using System.Globalization;
using System.Text;
using Investment.Backtest;
using Investment.Domain.Research;

namespace Investment.Research;

public static class ReportFormatter
{
    /// <summary>Research target from the project goal. Measured, never optimized for.</summary>
    public const double DailyReturnTarget = 0.01;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(RunOutcome o)
    {
        var r = o.Result;
        var sb = new StringBuilder();
        sb.AppendLine($"== {r.Strategy.Id} (logic v{r.Strategy.LogicVersion}) run {o.Run.Id}");
        sb.AppendLine($"   period {o.Run.StartDate:yyyy-MM-dd}..{o.Run.EndDate:yyyy-MM-dd} ({o.Net.TradingDays} sessions), params {r.Strategy.ParametersJson}");
        sb.AppendLine($"   data {r.DataHash[..12]} universe {r.UniverseHash[..12]} result {o.Run.ResultHash[..12]} code {o.Run.CodeCommit[..Math.Min(8, o.Run.CodeCommit.Length)]}{(o.Run.CodeDirty ? "+dirty" : "")}");
        if (r.HaltedOn is not null) sb.AppendLine($"   !! RISK HALT on {r.HaltedOn:yyyy-MM-dd}: {r.HaltReason}");
        sb.AppendLine();
        sb.AppendLine($"   {"metric",-26}{"gross",14}{"net",14}");
        void Row(string name, Func<BacktestMetric, string> f) => sb.AppendLine($"   {name,-26}{f(o.Gross),14}{f(o.Net),14}");
        Row("Total return", m => P(m.TotalReturn));
        Row("CAGR", m => P(m.Cagr));
        Row("Avg daily return", m => P(m.AvgDailyReturn, 3));
        Row("Daily return stdev", m => P(m.DailyReturnStdev, 3));
        Row("Sharpe (rf=0)", m => F(m.Sharpe));
        Row("Sortino", m => F(m.Sortino));
        Row("Max drawdown", m => P(m.MaxDrawdown));
        Row("Trades", m => m.NumberOfTrades.ToString(Inv));
        Row("Win rate", m => P(m.WinRate));
        Row("Profit factor", m => F(m.ProfitFactor));
        Row("Avg profit / trade", m => P(m.AvgProfit));
        Row("Avg loss / trade", m => P(m.AvgLoss));
        Row("EV / trade", m => P(m.ExpectedValuePerTrade, 3));
        Row("EV / trade (KRW)", m => m.ExpectedValuePerTradeKrw.ToString("N0", Inv));
        Row("EV t-stat", m => F(m.ExpectedValueTStat));
        Row("Annual turnover (x)", m => F(m.AnnualTurnover, 1));
        Row("Avg holding (sessions)", m => F(m.AvgHoldingSessions, 1));
        Row("Exposure", m => P(m.Exposure));
        Row("Costs / initial capital", m => P(m.TotalCosts));
        Row("Benchmark (KOSPI)", m => P(m.BenchmarkReturn));
        sb.AppendLine();
        sb.AppendLine($"   research target {P(DailyReturnTarget)} avg daily: net observed {P(o.Net.AvgDailyReturn, 3)} " +
                      $"({(o.Net.AvgDailyReturn >= DailyReturnTarget ? "at/above" : "below")} target)");

        sb.AppendLine("   net return by year: " + string.Join("  ", YearlyReturns(r).Select(y => $"{y.Year}:{P(y.Return)}")));
        sb.AppendLine("   exits: " + string.Join("  ", r.Trades.GroupBy(t => ExitGroup(t.ExitReason)).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()} (avg {P(g.Average(t => t.NetReturn))})")));
        if (r.Rejections.Count > 0)
            sb.AppendLine("   risk/fill rejections: " + string.Join("  ", r.Rejections.Select(kv => $"{kv.Key}={kv.Value}")));
        return sb.ToString();
    }

    public static IReadOnlyList<(int Year, double Return)> YearlyReturns(BacktestResult r)
    {
        var list = new List<(int, double)>();
        var prev = (double)r.Config.InitialCapital;
        foreach (var g in r.Equity.GroupBy(e => e.Date.Year))
        {
            var end = (double)g.Last().NetEquity;
            list.Add((g.Key, end / prev - 1));
            prev = end;
        }
        return list;
    }

    private static string ExitGroup(string reason) =>
        reason.StartsWith("rank", StringComparison.Ordinal) ? "rank-exit"
        : reason.StartsWith("held", StringComparison.Ordinal) ? "time-exit"
        : reason.StartsWith("close >", StringComparison.Ordinal) ? "reverted"
        : reason;

    public static string P(double x, int decimals = 2) =>
        double.IsNaN(x) ? "n/a" : (x * 100).ToString("F" + decimals, Inv) + "%";

    public static string F(double x, int decimals = 2) =>
        double.IsInfinity(x) ? "inf" : x.ToString("F" + decimals, Inv);
}
