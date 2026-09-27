namespace Investment.Domain.Market;

/// <summary>In-memory daily bar used by strategies and the simulator.</summary>
public readonly record struct Bar(
    DateOnly Date,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    bool IsHalted)
{
    public double TradingValue => Close * Volume;
}

/// <summary>Thrown when code tries to read data that was not yet available at the as-of time.</summary>
public sealed class LookAheadException(string message) : InvalidOperationException(message);

/// <summary>
/// Point-in-time view over a bar array: only bars with index &lt; <see cref="Count"/>
/// (i.e. dated on or before the as-of date) are reachable. Any attempt to read further
/// throws <see cref="LookAheadException"/>. This is the structural guard against look-ahead bias.
/// </summary>
public sealed class BarSeries
{
    private readonly Bar[] _bars;

    public BarSeries(string ticker, Bar[] bars, int visibleCount)
    {
        if (visibleCount < 0 || visibleCount > bars.Length)
            throw new ArgumentOutOfRangeException(nameof(visibleCount));
        Ticker = ticker;
        _bars = bars;
        Count = visibleCount;
    }

    public string Ticker { get; }

    /// <summary>Number of bars visible at the as-of date.</summary>
    public int Count { get; }

    public Bar this[int index]
    {
        get
        {
            if (index >= Count)
                throw new LookAheadException($"{Ticker}: bar index {index} is beyond the as-of boundary ({Count}).");
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            return _bars[index];
        }
    }

    public Bar Last => Count > 0 ? _bars[Count - 1] : throw new InvalidOperationException($"{Ticker}: no visible bars.");

    /// <summary>Bar <paramref name="barsAgo"/> sessions before the latest visible bar (0 = latest).</summary>
    public Bar Ago(int barsAgo) => this[Count - 1 - barsAgo];

    public double CloseAgo(int barsAgo) => Ago(barsAgo).Close;

    public ReadOnlySpan<Bar> Window(int length)
    {
        if (length > Count) throw new ArgumentOutOfRangeException(nameof(length), $"{Ticker}: only {Count} bars visible.");
        return new ReadOnlySpan<Bar>(_bars, Count - length, length);
    }

    public ReadOnlySpan<Bar> Visible => new(_bars, 0, Count);
}
