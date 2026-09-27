using Investment.Domain.Strategies;

namespace Investment.Strategies.MeanReversion;

public sealed record MeanReversionParameters
{
    public int BandLength { get; init; } = 20;

    /// <summary>Enter when (close − SMA) / SD over BandLength is at or below this.</summary>
    public double EntryZ { get; init; } = -2.0;

    /// <summary>Only buy dips of stocks above their long-term average (null disables the filter).</summary>
    public int? TrendLength { get; init; } = 200;

    /// <summary>Exit when close recovers above this short SMA.</summary>
    public int ExitSmaLength { get; init; } = 5;

    public int MaxHoldingSessions { get; init; } = 10;
}

/// <summary>
/// Short-term mean reversion. Hypothesis: sharp short-term sell-offs (≤ −2σ vs the 20-session mean) in
/// stocks still in a long-term uptrend are over-reactions that partially revert within days.
/// </summary>
public sealed class MeanReversionStrategy(MeanReversionParameters? parameters = null) : IStrategy
{
    public const string Id = "meanrev.zscore";

    private readonly MeanReversionParameters _p = parameters ?? new MeanReversionParameters();

    public StrategyDescriptor Descriptor => new(
        Id, "Z-score mean reversion", "MeanReversion", 1,
        "Oversold (z <= EntryZ over BandLength) stocks above their TrendLength SMA revert; exit above ExitSma or after MaxHolding.",
        _p);

    public int WarmupBars => Math.Max(_p.BandLength, _p.TrendLength ?? 0) + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars) continue;
            if (Indicators.AnyHalted(h, _p.BandLength)) continue; // halted closes distort the band
            var sd = Indicators.StdDev(h, _p.BandLength);
            if (sd <= 0) continue;
            var close = h.Last.Close;
            var z = (close - Indicators.Sma(h, _p.BandLength)) / sd;
            if (z > _p.EntryZ) continue;
            if (_p.TrendLength is { } tl && close <= Indicators.Sma(h, tl)) continue;
            signals.Add(new Signal(t, SignalAction.Buy, -z, $"z{_p.BandLength}={z:F2}" + (_p.TrendLength is { } l ? $" above SMA{l}" : "")));
        }

        foreach (var (ticker, pos) in ctx.Positions)
        {
            var h = ctx.History(ticker);
            if (h is null || h.Count < _p.ExitSmaLength) continue;
            if (h.Last.Close > Indicators.Sma(h, _p.ExitSmaLength))
                signals.Add(new Signal(ticker, SignalAction.Sell, 0, $"close > SMA{_p.ExitSmaLength} (reverted)"));
            else if (pos.HoldingSessions >= _p.MaxHoldingSessions)
                signals.Add(new Signal(ticker, SignalAction.Sell, 0, $"held {pos.HoldingSessions} >= {_p.MaxHoldingSessions}"));
        }
        return signals;
    }
}
