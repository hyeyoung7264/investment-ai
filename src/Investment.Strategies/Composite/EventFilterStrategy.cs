using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.Composite;

public sealed record EventFilterParameters
{
    public string Inner { get; init; } = "meanrev.zscore";
    public JsonObject? InnerParameters { get; init; }

    /// <summary>Buys are blocked for names with any of these events filed within LookbackDays.</summary>
    public IReadOnlyList<DisclosureEvent> BlockedEvents { get; init; } =
        [DisclosureEvent.RightsOffering, DisclosureEvent.ConvertibleBond, DisclosureEvent.BondWithWarrant];

    public int LookbackDays { get; init; } = 30;
}

/// <summary>
/// Wraps a strategy and drops its buys in names with recent blocked corporate events (e.g. dilutive financing:
/// a drop caused by dilution news is information, not an over-reaction). Sells always pass.
/// </summary>
public sealed class EventFilterStrategy : IStrategy
{
    public const string Id = "composite.event-filter";
    private readonly EventFilterParameters _p;
    private readonly IStrategy _inner;

    public EventFilterStrategy(EventFilterParameters? parameters = null)
    {
        _p = parameters ?? new EventFilterParameters();
        _inner = StrategyCatalog.Create(_p.Inner, _p.InnerParameters?.ToJsonString());
    }

    public StrategyDescriptor Descriptor => new(Id, $"Event filter over {_inner.Descriptor.Id}", "Composite", 1,
        $"{_inner.Descriptor.Id} without entries within {_p.LookbackDays}d of {string.Join('/', _p.BlockedEvents)}",
        new
        {
            _p.Inner,
            InnerParameters = JsonDocument.Parse(_inner.Descriptor.ParametersJson).RootElement,
            InnerLogicVersion = _inner.Descriptor.LogicVersion,
            _p.BlockedEvents,
            _p.LookbackDays,
        });

    public int WarmupBars => _inner.WarmupBars;
    public bool UsesCorporateEvents => true;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var since = ctx.AsOf.AddDays(-_p.LookbackDays);
        return _inner.GenerateSignals(ctx)
            .Where(s => s.Action != SignalAction.Buy || !ctx.Events(s.Ticker).Any(e => e.Date > since && _p.BlockedEvents.Contains(e.Type)))
            .ToList();
    }
}
