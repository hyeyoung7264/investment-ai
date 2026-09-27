using Investment.Domain.Strategies;

namespace Investment.Strategies.Control;

public sealed record LiquidityLeadersParameters
{
    public int TopK { get; init; } = 10;
    public int ExitRank { get; init; } = 20;
    public int Lookback { get; init; } = 60;
}

/// <summary>
/// Control: hold the most liquid names (≈ large caps). No alpha hypothesis — it checks the engine against
/// the index and is the passive baseline other strategies must beat after costs.
/// </summary>
public sealed class LiquidityLeadersStrategy(LiquidityLeadersParameters? parameters = null) : IStrategy
{
    public const string Id = "control.liquidity-leaders";
    private readonly LiquidityLeadersParameters _p = parameters ?? new LiquidityLeadersParameters();

    public StrategyDescriptor Descriptor => new(Id, "Liquidity leaders (control)", "Control", 1,
        "Control baseline: top-K by median trading value; no alpha claimed.", _p);

    public int WarmupBars => _p.Lookback;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var ranked = ctx.Universe
            .Select(t => (Ticker: t, H: ctx.History(t)))
            .Where(x => x.H is not null && x.H.Count >= _p.Lookback)
            .Select(x => (x.Ticker, Value: Indicators.MedianTradingValue(x.H!, _p.Lookback)))
            .OrderByDescending(x => x.Value).ThenBy(x => x.Ticker, StringComparer.Ordinal)
            .Select((x, i) => (x.Ticker, x.Value, Rank: i + 1))
            .ToList();
        var rank = ranked.ToDictionary(x => x.Ticker, x => x.Rank, StringComparer.Ordinal);
        var signals = ranked.Take(_p.TopK).Select(x => new Signal(x.Ticker, SignalAction.Buy, x.Value, $"liquidity rank {x.Rank}")).ToList();
        foreach (var held in ctx.Positions.Keys)
            if (!rank.TryGetValue(held, out var r) || r > _p.ExitRank)
                signals.Add(new Signal(held, SignalAction.Sell, 0, "liquidity rank dropped"));
        return signals;
    }
}

public sealed record RandomEntryParameters
{
    /// <summary>Daily probability that a universe name is flagged as a buy candidate.</summary>
    public double EntryProbability { get; init; } = 0.02;
    public int HoldingSessions { get; init; } = 5;
    public int Seed { get; init; } = 1;
}

/// <summary>
/// Control: random entries with a fixed holding period. Deterministic (seeded by date + ticker), so it is
/// reproducible. A strategy that does not beat this after costs has no demonstrated selection skill.
/// </summary>
public sealed class RandomEntryStrategy(RandomEntryParameters? parameters = null) : IStrategy
{
    public const string Id = "control.random";
    private readonly RandomEntryParameters _p = parameters ?? new RandomEntryParameters();

    public StrategyDescriptor Descriptor => new(Id, "Random entry (control)", "Control", 1,
        "Null baseline: random buys, fixed holding period; no alpha claimed.", _p);

    public int WarmupBars => 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe)
        {
            var u = Uniform(ctx.AsOf, t);
            if (u < _p.EntryProbability) signals.Add(new Signal(t, SignalAction.Buy, u, "random"));
        }
        foreach (var (t, pos) in ctx.Positions)
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        return signals;
    }

    private double Uniform(DateOnly d, string ticker)
    {
        // stable across runs and platforms (string.GetHashCode is randomized per process)
        ulong h = 1469598103934665603UL ^ (ulong)_p.Seed;
        foreach (var c in $"{d:yyyyMMdd}{ticker}") { h ^= c; h *= 1099511628211UL; }
        return (h >> 11) * (1.0 / (1UL << 53));
    }
}
