using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Domain.Market;
using Investment.Domain.Strategies;

namespace Investment.Strategies.Composite;

public sealed record RegimeFilterParameters
{
    public string Inner { get; init; } = "meanrev.zscore";
    public JsonObject? InnerParameters { get; init; }

    /// <summary>Trend regimes in which new entries are allowed (index regime known at the signal close).</summary>
    public IReadOnlyList<TrendRegime> AllowedTrends { get; init; } = [TrendRegime.Bull];

    /// <summary>Volatility regimes in which new entries are allowed (null = any).</summary>
    public IReadOnlyList<VolatilityRegime>? AllowedVolatility { get; init; }
}

/// <summary>
/// Wraps any strategy and only lets its Buy signals through in allowed market regimes; Sell signals always pass,
/// so positions opened earlier can still be closed. Regime comes from the point-in-time benchmark series.
/// </summary>
public sealed class RegimeFilterStrategy : IStrategy
{
    public const string Id = "composite.regime-filter";
    private readonly RegimeFilterParameters _p;
    private readonly IStrategy _inner;

    public RegimeFilterStrategy(RegimeFilterParameters? parameters = null)
    {
        _p = parameters ?? new RegimeFilterParameters();
        _inner = StrategyCatalog.Create(_p.Inner, _p.InnerParameters?.ToJsonString());
    }

    public StrategyDescriptor Descriptor => new(Id, $"Regime filter over {_inner.Descriptor.Id}", "Composite", 1,
        $"{_inner.Descriptor.Id} entries only in trend {string.Join('/', _p.AllowedTrends)}" +
        (_p.AllowedVolatility is null ? "" : $" and volatility {string.Join('/', _p.AllowedVolatility)}"),
        new
        {
            _p.Inner,
            InnerParameters = JsonDocument.Parse(_inner.Descriptor.ParametersJson).RootElement,
            InnerLogicVersion = _inner.Descriptor.LogicVersion,
            _p.AllowedTrends,
            _p.AllowedVolatility,
        });

    public int WarmupBars => _inner.WarmupBars;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext context)
    {
        var signals = _inner.GenerateSignals(context);
        var regime = context.MarketIndex is { } idx ? RegimeClassifier.Classify(idx) : null;
        var allowed = regime is { } r && _p.AllowedTrends.Contains(r.Trend) &&
                      (_p.AllowedVolatility is null || _p.AllowedVolatility.Contains(r.Volatility));
        if (allowed) return signals.Select(s => s.Action == SignalAction.Buy ? s with { Reason = $"{s.Reason} | regime {regime}" } : s).ToList();
        return signals.Where(s => s.Action != SignalAction.Buy).ToList();
    }
}
