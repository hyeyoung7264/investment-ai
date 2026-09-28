using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.EventDriven;

public sealed record EarningsDriftParameters
{
    /// <summary>Minimum standardized unexpected operating income to buy.</summary>
    public double MinSue { get; init; } = 2.0;
    public int FreshDays { get; init; } = 3;
    public int HoldingSessions { get; init; } = 20;
}

/// <summary>
/// Post-earnings-announcement drift. Hypothesis: prices under-react to large positive earnings surprises, so stocks
/// with SUE ≥ MinSue keep outperforming for weeks after the periodic report is filed. SUE uses only earlier filings.
/// </summary>
public sealed class EarningsDriftStrategy(EarningsDriftParameters? parameters = null) : IStrategy
{
    public const string Id = "event.pead";
    private readonly EarningsDriftParameters _p = parameters ?? new EarningsDriftParameters();

    public StrategyDescriptor Descriptor => new(Id, "Post-earnings drift", "EventDriven", 1,
        "Buy after a periodic report with SUE >= MinSue (filed within FreshDays), hold HoldingSessions.", _p);

    public int WarmupBars => 2;
    public bool UsesCorporateEvents => true;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var since = ctx.AsOf.AddDays(-_p.FreshDays);
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var ev = ctx.Events(t).LastOrDefault(e => e.Type == DisclosureEvent.EarningsReport && e.Date > since);
            if (ev.Value is not { } sue || sue < _p.MinSue) continue;
            signals.Add(new Signal(t, SignalAction.Buy, sue, $"SUE {sue:F2} filed {ev.Date:yyyy-MM-dd}: {ev.Title}"));
        }
        foreach (var (t, pos) in ctx.Positions)
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        return signals;
    }
}
