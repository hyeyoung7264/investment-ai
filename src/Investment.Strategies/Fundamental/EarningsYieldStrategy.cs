using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.Fundamental;

public sealed record EarningsYieldParameters
{
    /// <summary>Names held: top K by trailing operating income / market cap.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Held names are sold at a rebalance when their rank falls below this.</summary>
    public int ExitRank { get; init; } = 30;

    /// <summary>When true, hold only while the benchmark is above its 200-session average (trend filter).</summary>
    public bool TrendFilter { get; init; }
}

/// <summary>
/// Value (earnings yield). Hypothesis: among liquid stocks, high trailing operating income relative to market cap
/// outperforms (value premium). Rebalanced on the first session of each month → low turnover, so it can use the
/// capital that short-term strategies leave idle. Only filings and market caps known at the as-of date are used.
/// </summary>
public sealed class EarningsYieldStrategy(EarningsYieldParameters? parameters = null) : IStrategy
{
    public const string Id = "value.ey";
    private readonly EarningsYieldParameters _p = parameters ?? new EarningsYieldParameters();

    public StrategyDescriptor Descriptor => new(Id, "Earnings yield value", "Fundamental", 1,
        "Monthly (first session): hold top-K by TTM operating income / market cap (positive only); sell below ExitRank; optional trend filter.", _p);

    public int WarmupBars => 2;
    public bool UsesFundamentals => true;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var trendOk = !_p.TrendFilter || (ctx.MarketIndex is { Count: > 200 } idx && idx.Last.Close > Sma(idx, 200));
        if (!trendOk)
        {
            foreach (var t in ctx.Positions.Keys) signals.Add(new Signal(t, SignalAction.Sell, 0, "benchmark below SMA200"));
            return signals;
        }
        // rebalance at the close of the first session of each month (holiday-proof: taken from the index calendar)
        var idxs = ctx.MarketIndex;
        var firstOfMonth = idxs is null || idxs.Count < 2 || idxs.Ago(1).Date.Month != ctx.AsOf.Month;
        if (!firstOfMonth) return signals;

        var ranked = ctx.Universe
            .Select(t => (Ticker: t, F: ctx.Fundamentals(t)))
            .Where(x => x.F.TtmOperatingIncome is > 0 && x.F.MarketCap is > 0)
            .Select(x => (x.Ticker, Ey: x.F.TtmOperatingIncome!.Value / x.F.MarketCap!.Value))
            .OrderByDescending(x => x.Ey).ThenBy(x => x.Ticker, StringComparer.Ordinal)
            .Select((x, i) => (x.Ticker, x.Ey, Rank: i + 1)).ToList();
        var rank = ranked.ToDictionary(x => x.Ticker, x => x.Rank, StringComparer.Ordinal);
        foreach (var x in ranked.Take(_p.TopK))
            signals.Add(new Signal(x.Ticker, SignalAction.Buy, x.Ey, $"EY {x.Ey:P1} rank {x.Rank}/{ranked.Count}"));
        foreach (var t in ctx.Positions.Keys)
            if (!rank.TryGetValue(t, out var r) || r > _p.ExitRank)
                signals.Add(new Signal(t, SignalAction.Sell, 0, rank.ContainsKey(t) ? $"EY rank {r} > {_p.ExitRank}" : "no EY / left universe"));
        return signals;
    }

    private static double Sma(BarSeries s, int n)
    {
        var sum = 0.0;
        for (var k = 0; k < n; k++) sum += s.CloseAgo(k);
        return sum / n;
    }
}
