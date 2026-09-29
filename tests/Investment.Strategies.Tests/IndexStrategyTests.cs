using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Strategies.Index;

namespace Investment.Strategies.Tests;

public sealed class IndexStrategyTests
{
    private static Bar[] Series(IEnumerable<double> closes, DateOnly? start = null) =>
        closes.Select((c, i) => new Bar((start ?? new DateOnly(2024, 1, 1)).AddDays(i), c, c, c, c, 1000, false)).ToArray();

    private static StrategyContext Ctx(Bar[] etf, Bar[]? vkospi, IReadOnlyDictionary<string, HeldPosition>? held = null) =>
        new(etf[^1].Date, ["069500"], t => new BarSeries(t, etf, etf.Length), null, held ?? new Dictionary<string, HeldPosition>(),
            null, null, code => code == "VKOSPI" ? vkospi : null);

    [Fact]
    public void Vol_spike_buys_on_fear_and_sells_when_volatility_normalizes()
    {
        var etf = Series(Enumerable.Repeat(100.0, 70));
        var calm = Enumerable.Repeat(15.0, 69).ToList();
        var spike = Series([.. calm, 22.0]);   // 1.47x median
        var s = new VolatilitySpikeStrategy();
        Assert.Contains(s.GenerateSignals(Ctx(etf, spike)), x => x.Action == SignalAction.Buy);
        var normal = Series([.. calm, 15.0]);
        var held = new Dictionary<string, HeldPosition> { ["069500"] = new("069500", etf[0].Date, 100, 3) };
        var signals = s.GenerateSignals(Ctx(etf, normal, held));
        Assert.Contains(signals, x => x.Action == SignalAction.Sell);
        Assert.DoesNotContain(signals, x => x.Action == SignalAction.Buy);
        Assert.Equal(["VKOSPI"], s.AuxiliaryIndices);
    }

    [Fact]
    public void Vol_spike_never_reads_volatility_after_the_as_of_date()
    {
        var etf = Series(Enumerable.Repeat(100.0, 70));
        // spike happens the day AFTER the as-of date: must be invisible
        var v = Series([.. Enumerable.Repeat(15.0, 70), 40.0]);
        Assert.Empty(new VolatilitySpikeStrategy().GenerateSignals(Ctx(etf, v)));
    }

    [Fact]
    public void Trend_holds_above_the_moving_average_only()
    {
        var up = Series(Enumerable.Range(0, 250).Select(i => 100.0 + i));
        Assert.Contains(new IndexTrendStrategy().GenerateSignals(Ctx(up, null)), x => x.Action == SignalAction.Buy);
        var down = Series(Enumerable.Range(0, 250).Select(i => 400.0 - i));
        var held = new Dictionary<string, HeldPosition> { ["069500"] = new("069500", down[0].Date, 100, 30) };
        Assert.Contains(new IndexTrendStrategy().GenerateSignals(Ctx(down, null, held)), x => x.Action == SignalAction.Sell);
    }
}
