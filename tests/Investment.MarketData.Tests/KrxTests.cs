using Investment.MarketData.Krx;

namespace Investment.MarketData.Tests;

public sealed class KrxTests
{
    [Fact]
    public void Parses_official_daily_record_with_base_price_change()
    {
        var json = """
            {"OutBlock_1":[{"BAS_DD":"20260923","ISU_CD":"005930","ISU_NM":"삼성전자","MKT_NM":"KOSPI","SECT_TP_NM":"","TDD_CLSPRC":"285500","CMPPREVDD_PRC":"9000","FLUC_RT":"3.25","TDD_OPNPRC":"284500","TDD_HGPRC":"285500","TDD_LWPRC":"281000","ACC_TRDVOL":"20864376","ACC_TRDVAL":"5926486634990","MKTCAP":"1669112542584000","LIST_SHRS":"5846278608"}]}
            """;
        var r = Assert.Single(KrxClient.Parse(json, new DateOnly(2026, 9, 23)));
        Assert.Equal(("005930", 285_500m, 9_000m, 5_926_486_634_990m, 5_846_278_608L), (r.Ticker, r.Close, r.ChangeFromPrevious, r.TradingValue, r.ListedShares));
    }

    [Fact]
    public void Unauthorized_answers_are_errors_not_empty_days() =>
        Assert.Throws<KrxException>(() => KrxClient.Parse("""{"respMsg":"Unauthorized API Call","respCode":"401"}""", new DateOnly(2026, 9, 23)));
}

public sealed class KrxAdjusterTests
{
    private static Investment.Domain.Market.KrxDaily R(int day, decimal close, decimal change, long volume = 1000, decimal? open = null) => new()
    {
        Ticker = "T", Date = new DateOnly(2024, 1, day), Market = "KOSPI", Open = open ?? close, High = close, Low = close,
        Close = close, ChangeFromPrevious = change, Volume = volume,
    };

    [Fact]
    public void Split_uses_krx_base_price_to_adjust_history_and_volume()
    {
        // 2-for-1 split on day 3: base price 50 (= 51 - 1) vs previous close 100
        var bars = KrxPriceAdjuster.Build([R(1, 98, 0), R(2, 100, 2), R(3, 51, 1, 2000), R(4, 52, 1, 2000)]);
        Assert.Equal([49.0, 50.0, 51.0, 52.0], bars.Select(b => b.Close));
        Assert.Equal([2000L, 2000, 2000, 2000], bars.Select(b => b.Volume));
        Assert.Equal(100.0 * 1000, bars[1].Close * bars[1].Volume, 6); // trading value preserved
    }

    [Fact]
    public void Ordinary_days_are_untouched_and_halts_are_flagged()
    {
        var bars = KrxPriceAdjuster.Build([R(1, 100, 0), R(2, 110, 10), R(3, 110, 0, volume: 0, open: 0), R(4, 99, -11)]);
        Assert.Equal([100.0, 110.0, 110.0, 99.0], bars.Select(b => b.Close));
        Assert.True(bars[2].IsHalted);
        Assert.False(bars[3].IsHalted);
    }
}
