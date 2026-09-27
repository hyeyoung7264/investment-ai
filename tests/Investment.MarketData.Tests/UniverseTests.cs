using Investment.Domain.Market;
using Investment.MarketData.Quality;
using Investment.MarketData.Universe;

namespace Investment.MarketData.Tests;

public sealed class UniverseTests
{
    private static List<DateOnly> Calendar(DateOnly from, int sessions)
    {
        var list = new List<DateOnly>();
        for (var d = from; list.Count < sessions; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) list.Add(d);
        return list;
    }

    private static List<Bar> Bars(IEnumerable<DateOnly> dates, Func<int, double> close, Func<int, long> volume) =>
        dates.Select((d, i) => new Bar(d, close(i), close(i), close(i), close(i), volume(i), false)).ToList();

    private static Security Sec(string t, MarketType m = MarketType.Kospi, SecurityKind k = SecurityKind.Common, DateOnly? delisted = null) =>
        new() { Ticker = t, Name = t, Market = m, Kind = k, DelistedDate = delisted };

    [Fact]
    public void Selection_happens_strictly_before_effective_date_and_ranks_by_liquidity()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 300);
        var b = new UniverseBuilder(new UniverseDefinition { TopN = 2, MinHistoryBars = 20, LiquidityLookback = 20 }, cal, cal[100], cal[^1]);
        b.Add(Sec("A00000"), Bars(cal, _ => 10_000, _ => 1_000));
        b.Add(Sec("B00000"), Bars(cal, _ => 10_000, _ => 3_000));
        b.Add(Sec("C00000"), Bars(cal, _ => 10_000, _ => 2_000));
        var u = b.Build();

        Assert.All(u.Snapshots, s => Assert.True(s.SelectionDate < s.EffectiveFrom));
        Assert.Equal(["B00000", "C00000"], u.MembersOn(cal[150]));
        Assert.Empty(u.MembersOn(cal[0]));
    }

    [Fact]
    public void Future_liquidity_changes_do_not_affect_past_membership()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 300);
        UniverseBuilder Make() => new(new UniverseDefinition { TopN = 1, MinHistoryBars = 20, LiquidityLookback = 20 }, cal, cal[60], cal[^1]);

        var b1 = Make();
        b1.Add(Sec("A00000"), Bars(cal, _ => 10_000, _ => 1_000));
        b1.Add(Sec("B00000"), Bars(cal, _ => 10_000, _ => 500));
        var b2 = Make();
        b2.Add(Sec("A00000"), Bars(cal, _ => 10_000, _ => 1_000));
        // B becomes hugely liquid only from session 200 on
        b2.Add(Sec("B00000"), Bars(cal, _ => 10_000, i => i >= 200 ? 1_000_000 : 500));

        var u1 = b1.Build();
        var u2 = b2.Build();
        foreach (var d in cal.Where(d => d <= cal[200]))
            Assert.Equal(u1.MembersOn(d), u2.MembersOn(d));
        Assert.Equal(["B00000"], u2.MembersOn(cal[^1]));
    }

    [Fact]
    public void Excluded_kinds_markets_penny_stocks_and_short_histories_are_not_members()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 200);
        var b = new UniverseBuilder(new UniverseDefinition { TopN = 10, MinHistoryBars = 50, LiquidityLookback = 20 }, cal, cal[120], cal[^1]);
        b.Add(Sec("OK0000"), Bars(cal, _ => 5_000, _ => 1_000));
        b.Add(Sec("SPAC00", k: SecurityKind.Spac), Bars(cal, _ => 5_000, _ => 1_000));
        b.Add(Sec("KNX000", m: MarketType.Konex), Bars(cal, _ => 5_000, _ => 1_000));
        b.Add(Sec("PENNY0"), Bars(cal, _ => 500, _ => 1_000_000));
        b.Add(Sec("NEW000"), Bars(cal.Skip(170), _ => 5_000, _ => 1_000));
        Assert.Equal(["OK0000"], b.Build().MembersOn(cal[^1]));
    }

    [Fact]
    public void Delisted_security_is_member_while_it_traded()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 300);
        var delistedOn = cal[250];
        var b = new UniverseBuilder(new UniverseDefinition { TopN = 5, MinHistoryBars = 20, LiquidityLookback = 20 }, cal, cal[60], cal[^1]);
        b.Add(Sec("DEAD00", delisted: delistedOn), Bars(cal.Take(250), _ => 5_000, _ => 1_000));
        var u = b.Build();
        Assert.Contains("DEAD00", u.MembersOn(cal[150]));   // survivorship: it was in the universe back then
        Assert.DoesNotContain("DEAD00", u.MembersOn(cal[^1]));
    }

    [Fact]
    public void Monthly_schedule_starts_with_reconstitution_in_force_at_start()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 120);
        var sched = UniverseBuilder.MonthlySchedule(cal, new DateOnly(2020, 3, 15), cal[^1]);
        Assert.Equal(new DateOnly(2020, 3, 2), sched[0].Effective);
        Assert.Equal(new DateOnly(2020, 2, 28), sched[0].Selection);
    }

    [Fact]
    public void Quality_flags_beyond_limit_moves_and_inconsistent_ohlc()
    {
        var cal = Calendar(new DateOnly(2020, 1, 1), 5);
        var bars = new List<Bar>
        {
            new(cal[0], 100, 110, 90, 100, 10, false),
            new(cal[1], 100, 105, 101, 100, 10, false),   // low above open/close
            new(cal[2], 150, 150, 150, 150, 10, false),   // +50% (beyond ±30%)
        };
        var issues = DataQualityChecker.Check(Sec("X00000"), bars, cal);
        Assert.Contains(issues, i => i.Kind == QualityIssueKind.OhlcInconsistent && i.Date == cal[1]);
        Assert.Contains(issues, i => i.Kind == QualityIssueKind.ReturnBeyondPriceLimit && i.Date == cal[2]);
    }

    [Fact]
    public void Liquidation_trading_moves_are_real_but_other_limit_breaches_are_discontinuities()
    {
        var d = new DateOnly(2020, 3, 2);
        var prev = new Bar(d.AddDays(-1), 1000, 1000, 1000, 1000, 10, false);
        var crash = new Bar(d, 200, 200, 200, 200, 10, false);
        Assert.False(DataQualityChecker.IsDiscontinuity(Sec("L00000", delisted: d.AddDays(7)), prev, crash));
        Assert.True(DataQualityChecker.IsDiscontinuity(Sec("L00000", delisted: d.AddDays(90)), prev, crash));
        Assert.True(DataQualityChecker.IsDiscontinuity(Sec("L00000"), prev, crash));
        Assert.False(DataQualityChecker.IsDiscontinuity(Sec("L00000"), prev, new Bar(d, 1300, 1300, 1300, 1300, 10, false)));
    }
}
