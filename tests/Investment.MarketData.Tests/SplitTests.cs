using Investment.Domain.Market;
using Investment.MarketData.Splits;

namespace Investment.MarketData.Tests;

public sealed class SplitTests
{
    private static readonly DateOnly D = new(2018, 4, 27);

    [Fact]
    public void Forward_split_with_raw_source_volume_gets_volume_correction()
    {
        // Samsung 2018: chart price adjusted /50, chart volume left raw
        var pre = new Bar(D, 53380, 53639, 52440, 53000, 606_216, false);
        var post = new Bar(D.AddDays(7), 53000, 53900, 51800, 51900, 39_565_391, false);
        var v = SplitVerifier.Evaluate(pre, new RawQuote(D, 2_650_000, 606_216), post, new RawQuote(D.AddDays(7), 51_900, 39_565_391));
        Assert.True(v.IsEvent);
        Assert.Equal(0.02, v.PriceFactor, 6);
        Assert.False(v.SourceVolumeAdjusted);
        Assert.Equal(50, v.VolumeCorrection, 6);
    }

    [Fact]
    public void Reverse_split_with_adjusted_source_volume_needs_no_correction()
    {
        // 지엔코-like: 1-for-42 reverse split, chart adjusted both price (x42) and volume (/42)
        var pre = new Bar(D, 105_120, 105_120, 105_120, 105_120, 268_750, false);
        var post = new Bar(D.AddDays(7), 105_000, 105_000, 105_000, 105_000, 250_000, false);
        var v = SplitVerifier.Evaluate(pre, new RawQuote(D, 2_500, 11_300_863), post, new RawQuote(D.AddDays(7), 105_000, 250_000));
        Assert.True(v.IsEvent);
        Assert.Equal(42.048, v.PriceFactor, 3);
        Assert.True(v.SourceVolumeAdjusted);
        Assert.Equal(1, v.VolumeCorrection);
    }

    [Fact]
    public void Ordinary_halt_is_not_an_event()
    {
        var b = new Bar(D, 1000, 1000, 1000, 1000, 100, false);
        var v = SplitVerifier.Evaluate(b, new RawQuote(D, 1000, 100), b with { Date = D.AddDays(3) }, new RawQuote(D.AddDays(3), 1005, 300));
        Assert.False(v.IsEvent);
    }

    [Fact]
    public void Corrections_multiply_volume_only_before_each_event()
    {
        var bars = Enumerable.Range(0, 6).Select(i => new Bar(D.AddDays(i), 10, 10, 10, 10, 100, false)).ToList();
        var events = new List<SplitEvent>
        {
            new() { Ticker = "X", EventDate = D.AddDays(2), VolumeCorrection = 5 },
            new() { Ticker = "X", EventDate = D.AddDays(4), VolumeCorrection = 2 },
        };
        var fixedBars = SplitVerifier.ApplyVolumeCorrections(bars, events);
        Assert.Equal([1000L, 1000, 200, 200, 100, 100], fixedBars.Select(b => b.Volume));
    }

    [Fact]
    public void Candidates_are_first_sessions_after_halts_with_volume_regime_shift()
    {
        var bars = new List<Bar>();
        for (var i = 0; i < 50; i++) bars.Add(new Bar(D.AddDays(i), 10, 10, 10, 10, 100, false));
        for (var i = 50; i < 53; i++) bars.Add(new Bar(D.AddDays(i), 10, 10, 10, 10, 0, true));
        for (var i = 53; i < 100; i++) bars.Add(new Bar(D.AddDays(i), 10, 10, 10, 10, 5000, false));
        var c = Assert.Single(SplitVerifier.Candidates("X", bars));
        Assert.Equal(D.AddDays(49), c.PreDate);
        Assert.Equal(D.AddDays(53), c.EventDate);
    }

    [Fact]
    public void Raw_quote_page_parses_formatted_numbers() =>
        Assert.Equal(new RawQuote(new DateOnly(2018, 4, 27), 2_650_000m, 606_216),
            NaverRawQuoteSource.Parse("""[{"localTradedAt":"2018-04-27","closePrice":"2,650,000","accumulatedTradingVolume":606216}]""")[0]);
}
