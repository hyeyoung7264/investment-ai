using Investment.Domain.Market;
using Investment.MarketData;
using Investment.MarketData.Kind;
using Investment.MarketData.Naver;

namespace Investment.MarketData.Tests;

public sealed class ParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Kind_listed_rows_are_parsed_with_market_sector_and_date()
    {
        var rows = KindParser.ParseListed(Fixture("kind_listed_sample.html"));
        var samsung = Assert.Single(rows, r => r.Ticker == "005930");
        Assert.Equal("삼성전자", samsung.Name);
        Assert.Equal(MarketType.Kospi, samsung.Market);
        Assert.Equal("통신 및 방송 장비 제조업", samsung.Sector);
        Assert.NotNull(samsung.ListedDate);

        var alnum = Assert.Single(rows, r => r.Ticker == "0010S0");
        Assert.Equal(MarketType.Kosdaq, alnum.Market);
        Assert.Equal(new DateOnly(2026, 9, 23), alnum.ListedDate);
        Assert.Contains(rows, r => r.Market == MarketType.Konex);
    }

    [Fact]
    public void Kind_delisted_rows_map_company_code_to_common_ticker()
    {
        var html = Fixture("kind_delisted_sample.html");
        var rows = KindParser.ParseDelisted(html);
        Assert.Equal(5, rows.Count);
        var caprolactam = Assert.Single(rows, r => r.Name == "카프로");
        Assert.Equal("006380", caprolactam.Ticker);
        Assert.Equal(new DateOnly(2026, 9, 17), caprolactam.DelistedDate);
        Assert.Equal(MarketType.Kospi, caprolactam.Market);
        Assert.Contains("상장폐지", caprolactam.Reason);
        Assert.Equal("082660", rows[0].Ticker);
        Assert.NotNull(KindParser.ParseTotalCount(html));
    }

    [Theory]
    [InlineData("코스닥시장 이전상장", true)]
    [InlineData("유가증권시장 상장", true)]
    [InlineData("피흡수합병", false)]
    [InlineData("감사의견 거절(감사범위 제한)", false)]
    public void Market_transfers_are_not_delistings(string reason, bool transfer) =>
        Assert.Equal(transfer, new DelistedCompany("000000", "x", MarketType.Kosdaq, new DateOnly(2020, 1, 1), reason).IsMarketTransfer);

    [Fact]
    public void Naver_daily_normalizes_halted_sessions_and_keeps_raw_volume()
    {
        var bars = NaverParser.ParseDaily(Fixture("naver_daily_sample.json"));
        // 2018-04-30..05-03: Samsung halted for the 50:1 split; source reports O=H=L=0
        var halted = bars.Where(b => b.IsHalted).ToList();
        Assert.Equal(3, halted.Count);
        Assert.All(halted, b => Assert.True(b.Open == b.Close && b.High == b.Close && b.Low == b.Close));
        var first = bars.First();
        Assert.Equal(new DateOnly(2018, 4, 25), first.Date);
        Assert.Equal(50400m, first.Close); // split-adjusted price
        Assert.Equal(332292, first.Volume); // raw (unadjusted) volume
    }

    [Fact]
    public void Naver_rows_out_of_order_are_rejected() =>
        Assert.Throws<FormatException>(() => NaverParser.ParseDaily(
            """[{"localDate":"20200103","openPrice":1,"highPrice":1,"lowPrice":1,"closePrice":1,"accumulatedTradingVolume":1},{"localDate":"20200102","openPrice":1,"highPrice":1,"lowPrice":1,"closePrice":1,"accumulatedTradingVolume":1}]"""));

    [Theory]
    [InlineData("005930", "삼성전자", null, null, SecurityKind.Common)]
    [InlineData("005935", "삼성전자우", null, null, SecurityKind.Preferred)]
    [InlineData("0209J0", "KB제34호스팩", "금융 지원 서비스업", "기업인수합병", SecurityKind.Spac)]
    [InlineData("395400", "SK리츠", "부동산 임대 및 공급업", null, SecurityKind.Reit)]
    [InlineData("138040", "메리츠금융지주", "기타 금융업", null, SecurityKind.Common)]
    [InlineData("369370", "블리츠웨이엔터테인먼트", null, null, SecurityKind.Common)]
    [InlineData("027830", "대성창투", "신탁업 및 집합투자업", null, SecurityKind.Common)]
    [InlineData("088980", "맥쿼리인프라", "신탁업 및 집합투자업", null, SecurityKind.Fund)]
    [InlineData("083350", "동북아10호선박투자", null, null, SecurityKind.Fund)]
    public void Classifier(string ticker, string name, string? sector, string? products, SecurityKind expected) =>
        Assert.Equal(expected, SecurityClassifier.Classify(ticker, name, sector, products));
}
