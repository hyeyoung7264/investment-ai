using System.Text.Json;
using System.Text.Json.Nodes;
using Investment.Domain.Strategies;

namespace Investment.Strategies.Composite;

public sealed record PortfolioMember
{
    public required string Id { get; init; }
    public JsonObject? Parameters { get; init; }
}

public sealed record PortfolioParameters
{
    /// <summary>Members in priority order: when capital is short, earlier members' buys are filled first.</summary>
    public IReadOnlyList<PortfolioMember> Members { get; init; } = [];
}

/// <summary>
/// Several strategies sharing one capital pool under one risk engine (no leverage: gross exposure ≤ 100%).
/// Each position is owned by the member that bought it (tag in the entry reason); a member sees and sells
/// only its own positions. Stateless, so it works unchanged in paper trading.
/// </summary>
public sealed class PortfolioStrategy : IStrategy
{
    public const string Id = "composite.portfolio";
    private readonly List<(string Tag, IStrategy Strategy)> _members;
    private readonly PortfolioParameters _p;

    public PortfolioStrategy(PortfolioParameters? parameters = null)
    {
        _p = parameters ?? new PortfolioParameters();
        if (_p.Members.Count == 0) throw new ArgumentException("portfolio needs at least one member");
        _members = _p.Members.Select(m => ($"[{m.Id}]", StrategyCatalog.Create(m.Id, m.Parameters?.ToJsonString()))).ToList();
    }

    public StrategyDescriptor Descriptor => new(Id, "Shared-capital portfolio", "Composite", 1,
        $"Members {string.Join(" > ", _members.Select(m => m.Strategy.Descriptor.Id))} share one capital pool and risk budget.",
        new
        {
            Members = _members.Select(m => new
            {
                m.Strategy.Descriptor.Id,
                Parameters = JsonDocument.Parse(m.Strategy.Descriptor.ParametersJson).RootElement,
                m.Strategy.Descriptor.LogicVersion,
            }),
        });

    public int WarmupBars => _members.Max(m => m.Strategy.WarmupBars);

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var result = new List<Signal>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < _members.Count; k++)
        {
            var (tag, strategy) = _members[k];
            var own = ctx.Positions.Where(p => p.Value.EntryReason.StartsWith(tag, StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var view = new StrategyContext(ctx.AsOf, ctx.Universe, ctx.History, ctx.MarketIndex, own);
            var priority = (_members.Count - k) * 1_000_000.0;
            foreach (var s in strategy.GenerateSignals(view))
            {
                if (s.Action == SignalAction.Sell && !own.ContainsKey(s.Ticker)) continue;
                if (s.Action == SignalAction.Buy && (ctx.Positions.ContainsKey(s.Ticker) || !claimed.Add(s.Ticker))) continue;
                result.Add(s.Action == SignalAction.Buy ? s with { Score = priority + s.Score, Reason = $"{tag} {s.Reason}" } : s);
            }
        }
        return result;
    }
}
