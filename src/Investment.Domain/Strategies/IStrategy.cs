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

    /// <summary>True if the strategy reads <see cref="StrategyContext.Events"/> (disclosures are then loaded and hashed).</summary>
    bool UsesCorporateEvents => false;

    /// <summary>True if the strategy reads <see cref="StrategyContext.Fundamentals"/> (financials + market cap loaded and hashed).</summary>
    bool UsesFundamentals => false;
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
    IReadOnlyDictionary<string, HeldPosition> positions,
    Func<string, IReadOnlyList<CorporateEvent>>? events = null,
    Func<string, TickerFundamentals?>? fundamentals = null)
{
    private readonly Func<string, IReadOnlyList<CorporateEvent>>? _events = events;
    private readonly Func<string, TickerFundamentals?>? _fundamentals = fundamentals;

    public DateOnly AsOf { get; } = asOf;

    /// <summary>Tickers eligible for new entries today (point-in-time universe).</summary>
    public IReadOnlyList<string> Universe { get; } = universe;

    public BarSeries? MarketIndex { get; } = marketIndex;

    public IReadOnlyDictionary<string, HeldPosition> Positions { get; } = positions;

    /// <summary>History for a ticker, visible only up to <see cref="AsOf"/>; null if no data.</summary>
    public BarSeries? History(string ticker) => history(ticker);

    /// <summary>
    /// Corporate events of a ticker filed on or before <see cref="AsOf"/> (date-only receipts: usable for decisions
    /// made after that day's filing window, i.e. next-open entries). Future filings are never returned.
    /// </summary>
    public IReadOnlyList<CorporateEvent> Events(string ticker)
    {
        if (_events is null) return [];
        var all = _events(ticker);
        var n = 0;
        while (n < all.Count && all[n].Date <= AsOf) n++;
        return n == all.Count ? all : all.Take(n).ToList();
    }

    /// <summary>
    /// Trailing-twelve-month operating income and revenue from the last four quarters filed on or before
    /// <see cref="AsOf"/>, and the official market cap of the latest session on or before it.
    /// </summary>
    public FundamentalSnapshot Fundamentals(string ticker)
    {
        if (_fundamentals?.Invoke(ticker) is not { } f) return new FundamentalSnapshot(null, null, null, 0);
        var known = f.Quarters.Where(q => q.ReceiptDate <= AsOf)
            .GroupBy(q => (q.FiscalYear, q.Quarter)).Select(g => g.Last())
            .OrderBy(q => q.FiscalYear).ThenBy(q => q.Quarter).TakeLast(4).ToList();
        double? ttmOi = null, ttmRev = null;
        var consecutive = known.Count == 4 && (known[3].FiscalYear * 4 + known[3].Quarter) - (known[0].FiscalYear * 4 + known[0].Quarter) == 3;
        if (consecutive && known.All(q => q.OperatingIncome is not null)) ttmOi = known.Sum(q => (double)q.OperatingIncome!.Value);
        if (consecutive && known.All(q => q.Revenue is not null)) ttmRev = known.Sum(q => (double)q.Revenue!.Value);
        double? cap = null;
        var idx = Array.BinarySearch(f.CapDates, AsOf);
        if (idx < 0) idx = ~idx - 1;
        if (idx >= 0) cap = f.MarketCaps[idx];
        return new FundamentalSnapshot(ttmOi, ttmRev, cap, known.Count);
    }

    /// <summary>A copy with different positions (composites give each member only its own positions).</summary>
    public StrategyContext WithPositions(IReadOnlyDictionary<string, HeldPosition> positions) =>
        new(AsOf, Universe, history, MarketIndex, positions, _events, _fundamentals);
}
