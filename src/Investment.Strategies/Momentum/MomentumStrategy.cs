using Investment.Domain.Strategies;

namespace Investment.Strategies.Momentum;

public sealed record MomentumParameters
{
    /// <summary>Formation window in sessions.</summary>
    public int Lookback { get; init; } = 60;

    /// <summary>Most recent sessions excluded from the formation return (short-term reversal effect).</summary>
    public int Skip { get; init; } = 5;

    /// <summary>Names bought: top K by formation return.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Held names are sold when their rank falls below this.</summary>
    public int ExitRank { get; init; } = 20;
}

/// <summary>
/// Cross-sectional momentum. Hypothesis: among liquid Korean stocks, the strongest 60-session performers
/// (skipping the last 5 sessions) keep outperforming over the following weeks.
/// </summary>
public sealed class MomentumStrategy(MomentumParameters? parameters = null) : IStrategy
{
    public const string Id = "momentum.xsec";

    private readonly MomentumParameters _p = parameters ?? new MomentumParameters();

    public StrategyDescriptor Descriptor => new(
        Id, "Cross-sectional momentum", "Momentum", 1,
        "Top-K liquid stocks by (Lookback, Skip) return outperform over the next weeks; exit when rank drops below ExitRank.",
        _p);

    public int WarmupBars => _p.Lookback + _p.Skip + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var scored = new List<(string Ticker, double Score)>();
        foreach (var t in ctx.Universe)
        {
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars) continue;
            if (h.CloseAgo(_p.Lookback + _p.Skip) <= 0) continue;
            scored.Add((t, Indicators.Return(h, _p.Lookback + _p.Skip, _p.Skip)));
        }
        var ranked = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Ticker, StringComparer.Ordinal).ToList();
        var rank = ranked.Select((x, i) => (x.Ticker, Rank: i + 1)).ToDictionary(x => x.Ticker, x => x.Rank, StringComparer.Ordinal);

        var signals = new List<Signal>();
        foreach (var (ticker, score) in ranked.Take(_p.TopK))
            signals.Add(new Signal(ticker, SignalAction.Buy, score, $"rank {rank[ticker]}/{ranked.Count} r{_p.Lookback}s{_p.Skip}={score:P1}"));

        foreach (var held in ctx.Positions.Keys)
        {
            if (!rank.TryGetValue(held, out var r))
                signals.Add(new Signal(held, SignalAction.Sell, 0, "left universe / no score"));
            else if (r > _p.ExitRank)
                signals.Add(new Signal(held, SignalAction.Sell, 0, $"rank {r} > {_p.ExitRank}"));
        }
        return signals;
    }
}
