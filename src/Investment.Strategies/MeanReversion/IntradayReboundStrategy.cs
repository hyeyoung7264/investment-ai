using Investment.Domain.Strategies;

namespace Investment.Strategies.MeanReversion;

public sealed record IntradayReboundParameters
{
    public int BandLength { get; init; } = 20;
    public double EntryZ { get; init; } = -2.5;
}

/// <summary>
/// Same-session rebound. Hypothesis: most of the reversion after an extreme sell-off (z ≤ EntryZ) happens in the
/// next session, so holding open→close recycles capital daily without giving up the per-trade edge.
/// </summary>
public sealed class IntradayReboundStrategy(IntradayReboundParameters? parameters = null) : IStrategy
{
    public const string Id = "meanrev.intraday";
    private readonly IntradayReboundParameters _p = parameters ?? new IntradayReboundParameters();

    public StrategyDescriptor Descriptor => new(Id, "Intraday rebound after extreme sell-off", "MeanReversion", 1,
        "Names with z(BandLength) <= EntryZ are bought at the next open and sold at that session's close.", _p);

    public int WarmupBars => _p.BandLength + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe)
        {
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars || Indicators.AnyHalted(h, _p.BandLength)) continue;
            var sd = Indicators.StdDev(h, _p.BandLength);
            if (sd <= 0) continue;
            var z = (h.Last.Close - Indicators.Sma(h, _p.BandLength)) / sd;
            if (z <= _p.EntryZ) signals.Add(new Signal(t, SignalAction.Buy, -z, $"z{_p.BandLength}={z:F2} (day trade)", DayTrade: true));
        }
        return signals;
    }
}
