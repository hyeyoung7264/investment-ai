using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Strategies;
using Investment.Strategies.MeanReversion;
using Investment.Strategies.Momentum;

namespace Investment.Strategies.Tests;

public sealed class StrategyTests
{
    private static readonly DateOnly Start = new(2024, 1, 1);

    private static Bar[] Series(IEnumerable<double> closes) =>
        closes.Select((c, i) => new Bar(Start.AddDays(i), c, c, c, c, 1000, false)).ToArray();

    private static StrategyContext Ctx(Dictionary<string, Bar[]> data, IReadOnlyDictionary<string, HeldPosition>? held = null, int? visible = null)
    {
        var n = visible ?? data.Values.First().Length;
        return new StrategyContext(Start.AddDays(n - 1), data.Keys.ToList(),
            t => data.TryGetValue(t, out var b) ? new BarSeries(t, b, Math.Min(n, b.Length)) : null,
            null, held ?? new Dictionary<string, HeldPosition>());
    }

    [Fact]
    public void Momentum_buys_the_strongest_formation_returns_and_skips_recent_sessions()
    {
        var p = new MomentumParameters { Lookback = 10, Skip = 2, TopK = 1, ExitRank = 2 };
        // UP: steady rise; SPIKE: flat then jump only in the skipped last 2 sessions; DOWN: falling
        var up = Enumerable.Range(0, 20).Select(i => 100.0 + i * 5);
        var spike = Enumerable.Range(0, 20).Select(i => i >= 18 ? 500.0 : 100.0);
        var down = Enumerable.Range(0, 20).Select(i => 200.0 - i * 5);
        var data = new Dictionary<string, Bar[]> { ["UP"] = Series(up), ["SPIKE"] = Series(spike), ["DOWN"] = Series(down) };

        var signals = new MomentumStrategy(p).GenerateSignals(Ctx(data));
        var buy = Assert.Single(signals, s => s.Action == SignalAction.Buy);
        Assert.Equal("UP", buy.Ticker);
    }

    [Fact]
    public void Momentum_sells_held_names_whose_rank_drops()
    {
        var p = new MomentumParameters { Lookback = 5, Skip = 0, TopK = 1, ExitRank = 1 };
        var data = new Dictionary<string, Bar[]>
        {
            ["A"] = Series(Enumerable.Range(0, 10).Select(i => 100.0 + i)),
            ["B"] = Series(Enumerable.Range(0, 10).Select(i => 100.0 + i * 3)),
        };
        var held = new Dictionary<string, HeldPosition> { ["A"] = new("A", Start, 100, 3) };
        var signals = new MomentumStrategy(p).GenerateSignals(Ctx(data, held));
        Assert.Contains(signals, s => s.Ticker == "A" && s.Action == SignalAction.Sell);
        Assert.Contains(signals, s => s.Ticker == "B" && s.Action == SignalAction.Buy);
    }

    [Fact]
    public void Momentum_ignores_names_without_enough_history()
    {
        var data = new Dictionary<string, Bar[]> { ["NEW"] = Series(Enumerable.Repeat(100.0, 30)) };
        Assert.Empty(new MomentumStrategy().GenerateSignals(Ctx(data)));
    }

    [Fact]
    public void MeanReversion_buys_oversold_names_only_in_long_term_uptrend()
    {
        var p = new MeanReversionParameters { BandLength = 10, EntryZ = -2, TrendLength = 30, ExitSmaLength = 3 };
        // uptrend then a sharp one-day drop that stays above the 30-session mean
        var upDip = Enumerable.Range(0, 40).Select(i => 100.0 + i * 2).Append(155.0).ToArray(); // z≈-2.03, above SMA30≈150.2
        // downtrend with the same kind of drop: below the trend mean -> no buy
        var downDip = Enumerable.Range(0, 40).Select(i => 300.0 - i * 2).Append(200.0).ToArray();
        var data = new Dictionary<string, Bar[]> { ["UPDIP"] = Series(upDip), ["DOWNDIP"] = Series(downDip) };

        var signals = new MeanReversionStrategy(p).GenerateSignals(Ctx(data));
        var buy = Assert.Single(signals);
        Assert.Equal("UPDIP", buy.Ticker);
        Assert.Equal(SignalAction.Buy, buy.Action);
        Assert.True(buy.Score >= 2);
    }

    [Fact]
    public void MeanReversion_exits_on_reversion_or_time()
    {
        var p = new MeanReversionParameters { BandLength = 10, TrendLength = null, ExitSmaLength = 3, MaxHoldingSessions = 5 };
        var data = new Dictionary<string, Bar[]>
        {
            ["REV"] = Series([.. Enumerable.Repeat(100.0, 20), 90, 92, 99]),       // close above SMA3
            ["STUCK"] = Series([.. Enumerable.Repeat(100.0, 20), 90, 89, 88]),     // still falling, held too long
        };
        var held = new Dictionary<string, HeldPosition>
        {
            ["REV"] = new("REV", Start, 90, 2),
            ["STUCK"] = new("STUCK", Start, 90, 5),
        };
        var signals = new MeanReversionStrategy(p).GenerateSignals(Ctx(data, held));
        Assert.Contains(signals, s => s.Ticker == "REV" && s.Action == SignalAction.Sell && s.Reason.Contains("reverted"));
        Assert.Contains(signals, s => s.Ticker == "STUCK" && s.Action == SignalAction.Sell && s.Reason.Contains("held"));
    }

    [Fact]
    public void Strategies_only_see_the_visible_window()
    {
        // same arrays, but visibility cut before the dip: no signal must be produced
        var p = new MeanReversionParameters { BandLength = 10, EntryZ = -2, TrendLength = 30 };
        var bars = Series(Enumerable.Range(0, 40).Select(i => 100.0 + i * 2).Append(155.0));
        var data = new Dictionary<string, Bar[]> { ["X"] = bars };
        Assert.Empty(new MeanReversionStrategy(p).GenerateSignals(Ctx(data, visible: 40)));
        Assert.Single(new MeanReversionStrategy(p).GenerateSignals(Ctx(data, visible: 41)));
    }

    [Fact]
    public void Catalog_creates_versions_from_parameter_json()
    {
        var a = StrategyCatalog.Create(MomentumStrategy.Id);
        var b = StrategyCatalog.Create(MomentumStrategy.Id, """{"lookback":120}""");
        Assert.NotEqual(a.Descriptor.ParametersJson, b.Descriptor.ParametersJson);
        Assert.Contains("\"Lookback\":120", b.Descriptor.ParametersJson);
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("nope"));
    }
}
