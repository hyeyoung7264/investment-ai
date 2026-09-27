using System.Text.Json;
using Investment.Domain.Market;

namespace Investment.Domain.Strategies;

public enum SignalAction
{
    Hold = 0,
    Buy = 1,
    Sell = 2,
}

/// <summary>
/// Strategy output for one ticker at one as-of close. Buy = "want to hold", Sell = "want to exit".
/// Score orders buy candidates when capacity is limited. Reason is persisted as evidence.
/// DayTrade = a buy that is entered at the next open and closed at that same session's close.
/// </summary>
public sealed record Signal(string Ticker, SignalAction Action, double Score, string Reason, bool DayTrade = false);

/// <summary>Immutable identity of a strategy implementation + parameter set.</summary>
public sealed record StrategyDescriptor(
    string Id,
    string Name,
    string Family,
    int LogicVersion,
    string Hypothesis,
    object Parameters)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string ParametersJson => JsonSerializer.Serialize(Parameters, Parameters.GetType(), JsonOptions);
}

/// <summary>
/// A strategy only turns point-in-time market data into signals. It must not place orders,
/// size positions, persist data, or read anything outside <see cref="StrategyContext"/>.
/// </summary>
public interface IStrategy
{
    StrategyDescriptor Descriptor { get; }

    /// <summary>Minimum number of visible bars a ticker needs before the strategy may use it.</summary>
    int WarmupBars { get; }

    IReadOnlyList<Signal> GenerateSignals(StrategyContext context);
}

/// <summary>A held position as the strategy sees it. EntryReason carries the buy signal's reason (used for ownership tags).</summary>
public sealed record HeldPosition(string Ticker, DateOnly EntryDate, double EntryPrice, int HoldingSessions, string EntryReason = "");

/// <summary>
/// Everything a strategy may know at the close of <see cref="AsOf"/>. All series are
/// <see cref="BarSeries"/> views truncated at <see cref="AsOf"/>.
/// </summary>
public sealed class StrategyContext(
    DateOnly asOf,
    IReadOnlyList<string> universe,
    Func<string, BarSeries?> history,
    BarSeries? marketIndex,
    IReadOnlyDictionary<string, HeldPosition> positions)
{
    public DateOnly AsOf { get; } = asOf;

    /// <summary>Tickers eligible for new entries today (point-in-time universe).</summary>
    public IReadOnlyList<string> Universe { get; } = universe;

    public BarSeries? MarketIndex { get; } = marketIndex;

    public IReadOnlyDictionary<string, HeldPosition> Positions { get; } = positions;

    /// <summary>History for a ticker, visible only up to <see cref="AsOf"/>; null if no data.</summary>
    public BarSeries? History(string ticker) => history(ticker);
}
