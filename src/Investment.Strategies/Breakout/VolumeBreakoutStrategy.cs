using Investment.Domain.Strategies;

namespace Investment.Strategies.Breakout;

public sealed record VolumeBreakoutParameters
{
    public int Lookback { get; init; } = 20;
    public double VolumeMultiple { get; init; } = 3.0;
    public int HoldingSessions { get; init; } = 5;
    public int ExitSmaLength { get; init; } = 10;
}

/// <summary>
/// Volume-confirmed breakout. Hypothesis: a close at a new Lookback-session high on a strong up day with volume
/// ≥ VolumeMultiple × median volume signals information-driven buying that continues for several sessions.
/// </summary>
public sealed class VolumeBreakoutStrategy(VolumeBreakoutParameters? parameters = null) : IStrategy
{
    public const string Id = "breakout.volume";
    private readonly VolumeBreakoutParameters _p = parameters ?? new VolumeBreakoutParameters();

    public StrategyDescriptor Descriptor => new(Id, "Volume-confirmed breakout", "Breakout", 1,
        "Close at a Lookback high, close > open, volume >= VolumeMultiple x median: hold HoldingSessions or until close < SMA(ExitSmaLength).", _p);

    public int WarmupBars => Math.Max(_p.Lookback, _p.ExitSmaLength) + 2;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars || Indicators.AnyHalted(h, _p.Lookback + 1)) continue;
            var last = h.Last;
            if (last.Close <= last.Open) continue;
            var priorHigh = 0.0;
            var vols = new double[_p.Lookback];
            for (var k = 1; k <= _p.Lookback; k++)
            {
                var b = h.Ago(k);
                priorHigh = Math.Max(priorHigh, b.High);
                vols[k - 1] = b.Volume;
            }
            if (last.Close <= priorHigh) continue;
            Array.Sort(vols);
            var median = vols[_p.Lookback / 2];
            if (median <= 0 || last.Volume < _p.VolumeMultiple * median) continue;
            signals.Add(new Signal(t, SignalAction.Buy, last.Volume / median, $"{_p.Lookback}d high, volume x{last.Volume / median:F1}"));
        }
        foreach (var (t, pos) in ctx.Positions)
        {
            var h = ctx.History(t);
            if (h is null || h.Count < _p.ExitSmaLength) continue;
            if (pos.HoldingSessions >= _p.HoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
            else if (h.Last.Close < Indicators.Sma(h, _p.ExitSmaLength))
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"close < SMA{_p.ExitSmaLength}"));
        }
        return signals;
    }
}
