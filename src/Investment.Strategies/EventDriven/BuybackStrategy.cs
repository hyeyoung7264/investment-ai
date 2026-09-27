using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.EventDriven;

public sealed record BuybackParameters
{
    /// <summary>Calendar days an announcement stays actionable (covers weekend/holiday filings).</summary>
    public int FreshDays { get; init; } = 3;
    public int HoldingSessions { get; init; } = 20;
    public bool IncludeTrustContracts { get; init; }
}

/// <summary>
/// Buyback announcement drift. Hypothesis: open-market repurchase decisions signal undervaluation and add buying
/// pressure; prices keep drifting up for weeks after the announcement. Entry at the next open after the filing date.
/// </summary>
public sealed class BuybackStrategy(BuybackParameters? parameters = null) : IStrategy
{
    public const string Id = "event.buyback";
    private readonly BuybackParameters _p = parameters ?? new BuybackParameters();

    public StrategyDescriptor Descriptor => new(Id, "Buyback announcement drift", "EventDriven", 1,
        "Buy after a treasury-stock acquisition decision (filed within FreshDays), hold HoldingSessions.", _p);

    public int WarmupBars => 2;
    public bool UsesCorporateEvents => true;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var since = ctx.AsOf.AddDays(-_p.FreshDays);
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var ev = ctx.Events(t).LastOrDefault(e => e.Date > since &&
                (e.Type == DisclosureEvent.Buyback || (_p.IncludeTrustContracts && e.Type == DisclosureEvent.BuybackTrust)));
            if (ev.Title is null) continue;
            signals.Add(new Signal(t, SignalAction.Buy, ev.Date == ctx.AsOf ? 2 : 1, $"{ev.Type} filed {ev.Date:yyyy-MM-dd}: {ev.Title}"));
        }
        foreach (var (t, pos) in ctx.Positions)
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        return signals;
    }
}
