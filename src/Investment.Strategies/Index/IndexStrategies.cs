using Investment.Domain.Strategies;

namespace Investment.Strategies.Index;

public sealed record VolatilitySpikeParameters
{
    /// <summary>Buy when VKOSPI closes at or above this multiple of its median over MedianLength sessions.</summary>
    public double SpikeMultiple { get; init; } = 1.3;
    public int MedianLength { get; init; } = 60;
    public int MaxHoldingSessions { get; init; } = 20;
}

/// <summary>
/// Fear-spike rebound. Hypothesis: when implied volatility (VKOSPI) jumps far above its recent norm, the index is
/// oversold by forced de-risking and tends to recover; exit once implied volatility normalizes. Trades the universe
/// instrument (a 1x KOSPI 200 ETF).
/// </summary>
public sealed class VolatilitySpikeStrategy(VolatilitySpikeParameters? parameters = null) : IStrategy
{
    public const string Id = "index.volspike";
    private readonly VolatilitySpikeParameters _p = parameters ?? new VolatilitySpikeParameters();

    public StrategyDescriptor Descriptor => new(Id, "VKOSPI spike rebound", "Index", 1,
        "Buy the index ETF when VKOSPI >= SpikeMultiple x its MedianLength median; sell when VKOSPI <= median or after MaxHolding.", _p);

    public int WarmupBars => 2;
    public IReadOnlyList<string> AuxiliaryIndices => ["VKOSPI"];

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var v = ctx.Auxiliary("VKOSPI");
        if (v is null || v.Count < _p.MedianLength || v.Last.Date != ctx.AsOf) return signals;
        var window = new double[_p.MedianLength];
        for (var k = 0; k < _p.MedianLength; k++) window[k] = v.CloseAgo(k);
        Array.Sort(window);
        var median = window[_p.MedianLength / 2];
        var now = v.Last.Close;
        if (now >= _p.SpikeMultiple * median)
            foreach (var t in ctx.Universe.Where(t => !ctx.Positions.ContainsKey(t)))
                signals.Add(new Signal(t, SignalAction.Buy, now / median, $"VKOSPI {now:F1} = {now / median:F2}x median {median:F1}"));
        foreach (var (t, pos) in ctx.Positions)
        {
            if (now <= median) signals.Add(new Signal(t, SignalAction.Sell, 0, $"VKOSPI {now:F1} back to median {median:F1}"));
            else if (pos.HoldingSessions >= _p.MaxHoldingSessions) signals.Add(new Signal(t, SignalAction.Sell, 0, $"held {pos.HoldingSessions}"));
        }
        return signals;
    }
}

public sealed record TrendParameters
{
    public int SmaLength { get; init; } = 200;
}

/// <summary>
/// Index trend filter. Hypothesis: holding the index only while it is above its long moving average keeps most of
/// the market's return while avoiding the deepest drawdowns (time-series momentum / Faber 2007). Beta, not stock
/// selection: judged on drawdown and risk-adjusted return. Signals use the traded ETF's own series.
/// </summary>
public sealed class IndexTrendStrategy(TrendParameters? parameters = null) : IStrategy
{
    public const string Id = "index.trend";
    private readonly TrendParameters _p = parameters ?? new TrendParameters();

    public StrategyDescriptor Descriptor => new(Id, "Index trend filter", "Index", 1,
        "Hold the index ETF while its close is above SMA(SmaLength); otherwise cash.", _p);

    public int WarmupBars => _p.SmaLength + 1;

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        foreach (var t in ctx.Universe.Concat(ctx.Positions.Keys).Distinct())
        {
            var h = ctx.History(t);
            if (h is null || h.Count < WarmupBars) continue;
            var sma = Indicators.Sma(h, _p.SmaLength);
            var above = h.Last.Close > sma;
            if (above && !ctx.Positions.ContainsKey(t)) signals.Add(new Signal(t, SignalAction.Buy, h.Last.Close / sma, $"close > SMA{_p.SmaLength}"));
            if (!above && ctx.Positions.ContainsKey(t)) signals.Add(new Signal(t, SignalAction.Sell, 0, $"close <= SMA{_p.SmaLength}"));
        }
        return signals;
    }
}

public sealed record BasisParameters
{
    public int ZLength { get; init; } = 60;
    public double EntryZ { get; init; } = -2.0;
    public int MaxHoldingSessions { get; init; } = 10;
}

/// <summary>
/// Futures-basis contrarian. Hypothesis: an unusually negative KOSPI 200 basis (futures far below spot relative to its
/// recent range) reflects heavy hedging / pessimism that tends to be followed by an index rebound.
/// </summary>
public sealed class BasisStrategy(BasisParameters? parameters = null) : IStrategy
{
    public const string Id = "index.basis";
    private readonly BasisParameters _p = parameters ?? new BasisParameters();

    public StrategyDescriptor Descriptor => new(Id, "Futures basis contrarian", "Index", 1,
        "Buy the index ETF when the front-month basis z-score <= EntryZ; exit when z >= 0 or after MaxHolding.", _p);

    public int WarmupBars => 2;
    public IReadOnlyList<string> AuxiliaryIndices => ["K200BASIS"];

    public IReadOnlyList<Signal> GenerateSignals(StrategyContext ctx)
    {
        var signals = new List<Signal>();
        var b = ctx.Auxiliary("K200BASIS");
        if (b is null || b.Count < _p.ZLength || b.Last.Date != ctx.AsOf) return signals;
        var sd = Indicators.StdDev(b, _p.ZLength);
        if (sd <= 0) return signals;
        var z = (b.Last.Close - Indicators.Sma(b, _p.ZLength)) / sd;
        if (z <= _p.EntryZ)
            foreach (var t in ctx.Universe.Where(t => !ctx.Positions.ContainsKey(t)))
                signals.Add(new Signal(t, SignalAction.Buy, -z, $"basis {b.Last.Close:F0}bp z={z:F2}"));
        foreach (var (t, pos) in ctx.Positions)
            if (z >= 0 || pos.HoldingSessions >= _p.MaxHoldingSessions)
                signals.Add(new Signal(t, SignalAction.Sell, 0, z >= 0 ? $"basis normalized z={z:F2}" : $"held {pos.HoldingSessions}"));
        return signals;
    }
}
