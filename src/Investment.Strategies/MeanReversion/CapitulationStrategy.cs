using Investment.Domain.Strategies;

namespace Investment.Strategies.MeanReversion;

public sealed record CapitulationParameters
{
    public int BandLength { get; init; } = 20;
    public double EntryZ { get; init; } = -2.0;
    public double VolumeMultiple { get; init; } = 2.0;
    public int ExitSmaLength { get; init; } = 5;
    public int MaxHoldingSessions { get; init; } = 10;
}

/// <summary>
/// Capitulation reversal. Hypothesis: an extreme drop (z ≤ EntryZ) on heavy volume (≥ VolumeMultiple × median)
/// marks forced selling; forced-selling pressure reverts more reliably than quiet drift lower.
/// </summary>
public sealed class CapitulationStrategy(CapitulationParameters? parameters = null) : IStrategy
{
    public const string Id = "meanrev.capitulation";
    private readonly CapitulationParameters _p = parameters ?? new CapitulationParameters();

    public StrategyDescriptor Descriptor => new(Id, "Capitulation reversal", "MeanReversion", 1,
        "z(BandLength) <= EntryZ with volume >= VolumeMultiple x median; exit above SMA(ExitSmaLength) or after MaxHoldingSessions.", _p);

    public int WarmupBars => _p.BandLength + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe)
        {
            if (ctx.Positions.ContainsKey(t)) continue;
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars || Indicators.AnyHalted(h, _p.BandLength)) continue;
            var sd = Indicators.StdDev(h, _p.BandLength);
            if (sd <= 0) continue;
            var z = (h.Last.Close - Indicators.Sma(h, _p.BandLength)) / sd;
            if (z > _p.EntryZ) continue;
            var vols = new double[_p.BandLength];
            for (var k = 1; k <= _p.BandLength; k++) vols[k - 1] = h.Ago(k).Volume;
            Array.Sort(vols);
            var median = vols[_p.BandLength / 2];
            if (median <= 0 || h.Last.Volume < _p.VolumeMultiple * median) continue;
            signals.Add(new Signal(t, SignalAction.Buy, -z, $"z{_p.BandLength}={z:F2}, volume x{h.Last.Volume / median:F1}"));
        }
        foreach (var (t, pos) in ctx.Positions)
        {
            var h = ctx.History(t);
            if (h is null || h.Count < _p.ExitSmaLength) continue;
            if (h.Last.Close > Indicators.Sma(h, _p.ExitSmaLength))
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"close > SMA{_p.ExitSmaLength} (reverted)"));
            else if (pos.HoldingSessions >= _p.MaxHoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        }
        return signals;
    }
}
