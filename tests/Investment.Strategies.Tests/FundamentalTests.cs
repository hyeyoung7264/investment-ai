using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.Strategies.Fundamental;

namespace Investment.Strategies.Tests;

public sealed class FundamentalTests
{
    private static QuarterResult Q(string t, int y, int q, string filed, decimal oi) =>
        new(t, y, q, DateOnly.Parse(filed), "CFS", oi * 10, oi, oi, null);

    private static TickerFundamentals F(QuarterResult[] qs, double cap, string capDate = "2024-01-02") =>
        new(qs, [DateOnly.Parse(capDate)], [cap]);

    private static StrategyContext Ctx(DateOnly asOf, Dictionary<string, TickerFundamentals> f, Bar[]? index = null) =>
        new(asOf, f.Keys.ToList(), _ => null, index is null ? null : new BarSeries("I", index, index.Length),
            new Dictionary<string, HeldPosition>(), null, t => f.GetValueOrDefault(t));

    private static readonly QuarterResult[] FourQuarters =
    [
        Q("A", 2023, 1, "2023-05-15", 10), Q("A", 2023, 2, "2023-08-14", 10),
        Q("A", 2023, 3, "2023-11-14", 10), Q("A", 2023, 4, "2024-03-20", 20),
    ];

    [Fact]
    public void Ttm_uses_only_quarters_filed_by_the_as_of_date()
    {
        var f = new Dictionary<string, TickerFundamentals> { ["A"] = F(FourQuarters, 1000) };
        Assert.Null(Ctx(new DateOnly(2024, 3, 19), f).Fundamentals("A").TtmOperatingIncome); // Q4 not yet filed → only 3 + older missing
        var snap = Ctx(new DateOnly(2024, 3, 20), f).Fundamentals("A");
        Assert.Equal((50.0, 1000.0, 4), (snap.TtmOperatingIncome!.Value, snap.MarketCap!.Value, snap.QuartersKnown));
    }

    [Fact]
    public void Ttm_requires_four_consecutive_quarters()
    {
        QuarterResult[] gap = [FourQuarters[0], FourQuarters[1], FourQuarters[3], Q("A", 2024, 1, "2024-05-15", 10)];
        var f = new Dictionary<string, TickerFundamentals> { ["A"] = F(gap, 1000) };
        Assert.Null(Ctx(new DateOnly(2024, 6, 1), f).Fundamentals("A").TtmOperatingIncome);
    }

    [Fact]
    public void Value_strategy_buys_highest_earnings_yield_on_first_session_of_month_only()
    {
        QuarterResult[] Qs(string t, decimal oi) => [.. FourQuarters.Select(q => q with { Ticker = t, OperatingIncome = oi })];
        var f = new Dictionary<string, TickerFundamentals>
        {
            ["CHEAP"] = F(Qs("CHEAP", 25), 1000),   // EY 10%
            ["DEAR"] = F(Qs("DEAR", 5), 1000),      // EY 2%
            ["LOSS"] = F(Qs("LOSS", -5), 1000),     // excluded
        };
        var index = new[] { new Bar(new DateOnly(2024, 3, 29), 1, 1, 1, 1, 0, false), new Bar(new DateOnly(2024, 4, 1), 1, 1, 1, 1, 0, false) };
        var s = new EarningsYieldStrategy(new EarningsYieldParameters { TopK = 1 });
        var buys = s.GenerateSignals(Ctx(new DateOnly(2024, 4, 1), f, index)).Where(x => x.Action == SignalAction.Buy).ToList();
        Assert.Equal("CHEAP", Assert.Single(buys).Ticker);
        var midMonth = new[] { index[1], new Bar(new DateOnly(2024, 4, 2), 1, 1, 1, 1, 0, false) };
        Assert.Empty(s.GenerateSignals(Ctx(new DateOnly(2024, 4, 2), f, midMonth)));
    }
}
