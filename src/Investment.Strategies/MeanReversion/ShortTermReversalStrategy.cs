using Investment.Domain.Strategies;

namespace Investment.Strategies.MeanReversion;

public sealed record ShortTermReversalParameters
{
    public int Lookback { get; init; } = 5;
    public int TopK { get; init; } = 10;
    public int HoldingSessions { get; init; } = 5;

    /// <summary>Losers beyond this are skipped (likely news-driven collapses, not over-reaction).</summary>
    public double MaxLoss { get; init; } = -0.30;
}

/// <summary>
/// Short-term reversal. Hypothesis: among liquid Korean stocks, the biggest losers of the last week outperform
/// over the next week (liquidity-provision / over-reaction premium documented for KRX).
/// </summary>
public sealed class ShortTermReversalStrategy(ShortTermReversalParameters? parameters = null) : IStrategy
{
    public const string Id = "reversal.st";
    private readonly ShortTermReversalParameters _p = parameters ?? new ShortTermReversalParameters();

    public StrategyDescriptor Descriptor => new(Id, "Short-term reversal", "MeanReversion", 1,
        "Bottom-K liquid stocks by Lookback-session return (above MaxLoss) outperform over the next HoldingSessions.", _p);

    public int WarmupBars => _p.Lookback + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var losers = new List<(string Ticker, double Ret)>();
        foreach (var t in ctx.Universe)
        {
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars || Indicators.AnyHalted(h, _p.Lookback + 1)) continue;
            var r = Indicators.Return(h, _p.Lookback, 0);
            if (r < 0 && r > _p.MaxLoss) losers.Add((t, r));
        }
        var signals = losers.OrderBy(x => x.Ret).ThenBy(x => x.Ticker, StringComparer.Ordinal).Take(_p.TopK)
            .Select(x => new Signal(x.Ticker, SignalAction.Buy, -x.Ret, $"r{_p.Lookback}={x.Ret:P1}")).ToList();
        foreach (var (t, pos) in ctx.Positions)
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        return signals;
    }
}
