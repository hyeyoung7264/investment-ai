using System.Text.Json;
using Investment.Domain.Strategies;
using Investment.Strategies.Breakout;
using Investment.Strategies.Composite;
using Investment.Strategies.Control;
using Investment.Strategies.EventDriven;
using Investment.Strategies.Fundamental;
using Investment.Strategies.Index;
using Investment.Strategies.MeanReversion;
using Investment.Strategies.Momentum;

namespace Investment.Strategies;

/// <summary>Registry of strategy implementations. Parameters come from JSON so each set is a distinct version.</summary>
public static class StrategyCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static readonly Dictionary<string, Func<string?, IStrategy>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        [MomentumStrategy.Id] = json => new MomentumStrategy(Parse<MomentumParameters>(json)),
        [MeanReversionStrategy.Id] = json => new MeanReversionStrategy(Parse<MeanReversionParameters>(json)),
        [LiquidityLeadersStrategy.Id] = json => new LiquidityLeadersStrategy(Parse<LiquidityLeadersParameters>(json)),
        [RandomEntryStrategy.Id] = json => new RandomEntryStrategy(Parse<RandomEntryParameters>(json)),
        [ShortTermReversalStrategy.Id] = json => new ShortTermReversalStrategy(Parse<ShortTermReversalParameters>(json)),
        [IntradayReboundStrategy.Id] = json => new IntradayReboundStrategy(Parse<IntradayReboundParameters>(json)),
        [CapitulationStrategy.Id] = json => new CapitulationStrategy(Parse<CapitulationParameters>(json)),
        [VolumeBreakoutStrategy.Id] = json => new VolumeBreakoutStrategy(Parse<VolumeBreakoutParameters>(json)),
        [EarningsDriftStrategy.Id] = json => new EarningsDriftStrategy(Parse<EarningsDriftParameters>(json)),
        [EarningsYieldStrategy.Id] = json => new EarningsYieldStrategy(Parse<EarningsYieldParameters>(json)),
        [VolatilitySpikeStrategy.Id] = json => new VolatilitySpikeStrategy(Parse<VolatilitySpikeParameters>(json)),
        [BasisStrategy.Id] = json => new BasisStrategy(Parse<BasisParameters>(json)),
        [IndexTrendStrategy.Id] = json => new IndexTrendStrategy(Parse<TrendParameters>(json)),
        [BuybackStrategy.Id] = json => new BuybackStrategy(Parse<BuybackParameters>(json)),
        [EventFilterStrategy.Id] = json => new EventFilterStrategy(Parse<EventFilterParameters>(json)),
        [PortfolioStrategy.Id] = json => new PortfolioStrategy(Parse<PortfolioParameters>(json)),
        [RegimeFilterStrategy.Id] = json => new RegimeFilterStrategy(Parse<RegimeFilterParameters>(json)),
    };

    public static IReadOnlyCollection<string> Ids => Factories.Keys;

    public static IStrategy Create(string id, string? parametersJson = null) =>
        Factories.TryGetValue(id, out var f) ? f(parametersJson) : throw new ArgumentException($"unknown strategy '{id}'. Known: {string.Join(", ", Ids)}");

    private static T? Parse<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
}
