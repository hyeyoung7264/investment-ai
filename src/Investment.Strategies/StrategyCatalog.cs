using System.Text.Json;
using Investment.Domain.Strategies;
using Investment.Strategies.Control;
using Investment.Strategies.MeanReversion;
using Investment.Strategies.Momentum;

namespace Investment.Strategies;

/// <summary>Registry of strategy implementations. Parameters come from JSON so each set is a distinct version.</summary>
public static class StrategyCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private static readonly Dictionary<string, Func<string?, IStrategy>> Factories = new(StringComparer.OrdinalIgnoreCase)
    {
        [MomentumStrategy.Id] = json => new MomentumStrategy(Parse<MomentumParameters>(json)),
        [MeanReversionStrategy.Id] = json => new MeanReversionStrategy(Parse<MeanReversionParameters>(json)),
        [LiquidityLeadersStrategy.Id] = json => new LiquidityLeadersStrategy(Parse<LiquidityLeadersParameters>(json)),
        [RandomEntryStrategy.Id] = json => new RandomEntryStrategy(Parse<RandomEntryParameters>(json)),
    };

    public static IReadOnlyCollection<string> Ids => Factories.Keys;

    public static IStrategy Create(string id, string? parametersJson = null) =>
        Factories.TryGetValue(id, out var f) ? f(parametersJson) : throw new ArgumentException($"unknown strategy '{id}'. Known: {string.Join(", ", Ids)}");

    private static T? Parse<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
}
