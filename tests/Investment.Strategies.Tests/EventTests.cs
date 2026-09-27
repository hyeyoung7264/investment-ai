using Investment.Domain.Market;
using Investment.Domain.Strategies;
using Investment.MarketData.Dart;
using Investment.Strategies.Composite;
using Investment.Strategies.EventDriven;

namespace Investment.Strategies.Tests;

public sealed class EventTests
{
    private static readonly DateOnly D = new(2024, 3, 15);
    private static readonly Bar[] Flat = Enumerable.Range(0, 60).Select(i => new Bar(D.AddDays(i - 59), 100, 101, 99, 100, 1000, false)).ToArray();

    private static StrategyContext Ctx(DateOnly asOf, Dictionary<string, CorporateEvent[]> events, IReadOnlyDictionary<string, HeldPosition>? held = null) =>
        new(asOf, events.Keys.ToList(), t => new BarSeries(t, Flat, Flat.Length), null, held ?? new Dictionary<string, HeldPosition>(),
            t => events.TryGetValue(t, out var e) ? e : []);

    [Fact]
    public void Context_never_returns_events_filed_after_the_as_of_date()
    {
        var events = new Dictionary<string, CorporateEvent[]>
        {
            ["A"] = [new(D.AddDays(-2), DisclosureEvent.Buyback, "past"), new(D, DisclosureEvent.Buyback, "today"), new(D.AddDays(1), DisclosureEvent.RightsOffering, "future")],
        };
        var seen = Ctx(D, events).Events("A");
        Assert.Equal(["past", "today"], seen.Select(e => e.Title));
    }

    [Fact]
    public void Buyback_buys_fresh_announcements_and_exits_after_holding_period()
    {
        var events = new Dictionary<string, CorporateEvent[]>
        {
            ["NEW"] = [new(D, DisclosureEvent.Buyback, "주요사항보고서(자기주식취득결정)")],
            ["OLD"] = [new(D.AddDays(-10), DisclosureEvent.Buyback, "stale")],
            ["TRUST"] = [new(D, DisclosureEvent.BuybackTrust, "trust")],
        };
        var held = new Dictionary<string, HeldPosition> { ["H"] = new("H", D.AddDays(-30), 100, 20) };
        var signals = new BuybackStrategy().GenerateSignals(Ctx(D, events, held));
        Assert.Contains(signals, s => s.Ticker == "NEW" && s.Action == SignalAction.Buy);
        Assert.DoesNotContain(signals, s => s.Ticker == "OLD");
        Assert.DoesNotContain(signals, s => s.Ticker == "TRUST");
        Assert.Contains(signals, s => s.Ticker == "H" && s.Action == SignalAction.Sell);
        Assert.True(new BuybackStrategy().UsesCorporateEvents);
    }

    [Fact]
    public void Event_filter_blocks_buys_after_dilutive_financing_only()
    {
        var events = new Dictionary<string, CorporateEvent[]>
        {
            ["DIL"] = [new(D.AddDays(-5), DisclosureEvent.ConvertibleBond, "CB")],
            ["OLDDIL"] = [new(D.AddDays(-60), DisclosureEvent.RightsOffering, "old")],
            ["CLEAN"] = [],
        };
        var filter = new EventFilterStrategy(new EventFilterParameters
        {
            Inner = "control.random",
            InnerParameters = System.Text.Json.Nodes.JsonNode.Parse("""{"EntryProbability":1.0}""")!.AsObject(),
        });
        var buys = filter.GenerateSignals(Ctx(D, events)).Where(s => s.Action == SignalAction.Buy).Select(s => s.Ticker).Order().ToList();
        Assert.Equal(["CLEAN", "OLDDIL"], buys);
    }

    [Theory]
    [InlineData("주요사항보고서(자기주식취득결정)", DisclosureEvent.Buyback, false)]
    [InlineData("[기재정정]주요사항보고서(유상증자결정)", DisclosureEvent.RightsOffering, true)]
    [InlineData("주요사항보고서(무상증자결정)", DisclosureEvent.BonusIssue, false)]
    [InlineData("주요사항보고서(유무상증자결정)", DisclosureEvent.RightsOffering, false)]
    [InlineData("단일판매ㆍ공급계약체결", DisclosureEvent.SupplyContract, false)]
    [InlineData("자기주식취득신탁계약체결결정", DisclosureEvent.BuybackTrust, false)]
    [InlineData("주요사항보고서(전환사채권발행결정)", DisclosureEvent.ConvertibleBond, false)]
    [InlineData("기업설명회(IR)개최", DisclosureEvent.Other, false)]
    public void Classifier_maps_report_names(string name, DisclosureEvent expected, bool correction) =>
        Assert.Equal((expected, correction), DisclosureClassifier.Classify(name));

    [Fact]
    public void Dart_list_parser_reads_items_and_treats_013_as_empty()
    {
        var json = """{"status":"000","message":"정상","page_no":1,"page_count":100,"total_count":1,"total_page":1,"list":[{"corp_code":"00126380","corp_name":"삼성전자","stock_code":"005930","corp_cls":"Y","report_nm":"주요사항보고서(자기주식취득결정) ","rcept_no":"20241115000123","flr_nm":"삼성전자","rcept_dt":"20241115","rm":"유"}]}""";
        var (items, pages) = DartClient.Parse(json, "B");
        var d = Assert.Single(items);
        Assert.Equal((DisclosureEvent.Buyback, "005930", new DateOnly(2024, 11, 15)), (d.Event, d.Ticker, d.ReceiptDate));
        Assert.Equal(1, pages);
        Assert.Empty(DartClient.Parse("""{"status":"013","message":"조회된 데이타가 없습니다."}""", "B").Items);
        Assert.Throws<DartException>(() => DartClient.Parse("""{"status":"020","message":"요청 제한을 초과하였습니다."}""", "B"));
    }
}
