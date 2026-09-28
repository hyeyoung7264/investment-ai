using Investment.Domain.Market;
using Investment.MarketData.Dart;

namespace Investment.MarketData.Tests;

public sealed class EarningsTests
{
    private static FinancialReportLine L(int year, string code, string account, decimal? cur, decimal? prior, string date,
        decimal? cum = null, decimal? priorCum = null, string fs = "CFS") => new()
    {
        CorpCode = "C1", Ticker = "T00010", FiscalYear = year, ReportCode = code, FsDiv = fs, Account = account,
        ThisAmount = cur, PriorAmount = prior, ThisCumulative = cum, PriorCumulative = priorCum,
        ReceiptNo = date.Replace("-", "") + "000001", ReceiptDate = DateOnly.Parse(date),
    };

    [Fact]
    public void Q4_is_annual_minus_nine_month_cumulative()
    {
        var q = EarningsCalculator.Quarters(
        [
            L(2024, "11014", "OperatingIncome", 30, 20, "2024-11-14", cum: 90, priorCum: 60),
            L(2024, "11011", "OperatingIncome", 130, 85, "2025-03-18"),
        ]);
        var q4 = Assert.Single(q, x => x.Quarter == 4);
        Assert.Equal((40m, 25m), (q4.OperatingIncome!.Value, q4.PriorOperatingIncome!.Value));
        Assert.Equal(new DateOnly(2025, 3, 18), q4.ReceiptDate);
    }

    [Fact]
    public void Consolidated_figures_are_preferred_over_separate()
    {
        var q = EarningsCalculator.Quarters(
        [
            L(2024, "11013", "OperatingIncome", 10, 5, "2024-05-14", fs: "OFS"),
            L(2024, "11013", "OperatingIncome", 12, 6, "2024-05-14", fs: "CFS"),
        ]);
        Assert.Equal(("CFS", 12m), (Assert.Single(q).FsDiv, q[0].OperatingIncome!.Value));
    }

    [Fact]
    public void Sue_uses_only_quarters_filed_before_the_event()
    {
        // changes of +1,-1,+1,-1 then a +10 jump; a later-filed amendment of an older quarter must not leak in
        var quarters = new List<QuarterResult>();
        var date = new DateOnly(2023, 5, 15);
        decimal[] changes = [1, -1, 1, -1, 10];
        for (var i = 0; i < changes.Length; i++)
            quarters.Add(new QuarterResult("T00010", 2023 + i / 4, i % 4 + 1, date.AddDays(91 * i), "CFS", null, 100 + changes[i], 100, null));
        var sue = EarningsCalculator.Surprises(quarters);
        var last = Assert.Single(sue);
        var sd = Math.Sqrt(4.0 / 3.0); // sample stdev of {1,-1,1,-1}
        Assert.Equal(10 / sd, last.Sue, 6);

        // same data, but the jump quarter's predecessor was filed AFTER it: history shrinks below the minimum
        quarters[3] = quarters[3] with { ReceiptDate = quarters[4].ReceiptDate.AddDays(1) };
        Assert.DoesNotContain(EarningsCalculator.Surprises(quarters), s => s.Quarter.Quarter == quarters[4].Quarter && s.Quarter.FiscalYear == quarters[4].FiscalYear);
    }

    [Theory]
    [InlineData(2024, 3, "2024-11-14", true)]
    [InlineData(2024, 4, "2025-03-31", true)]
    [InlineData(2022, 3, "2024-06-01", false)] // amendment years later: stale
    public void Only_timely_filings_are_earnings_announcements(int year, int quarter, string filed, bool timely) =>
        Assert.Equal(timely, EarningsCalculator.IsTimely(new QuarterResult("T", year, quarter, DateOnly.Parse(filed), "CFS", null, 1, 1, null)));

    [Fact]
    public void Key_account_parser_maps_accounts_and_receipt_date()
    {
        var json = """
            {"status":"000","message":"정상","list":[
            {"rcept_no":"20241114002642","bsns_year":"2024","corp_code":"00126380","stock_code":"005930","reprt_code":"11014","account_nm":"영업이익","fs_div":"CFS","thstrm_amount":"9,183,371,000,000","thstrm_add_amount":"26,233,258,000,000","frmtrm_amount":"2,433,534,000,000","frmtrm_add_amount":"3,742,259,000,000"},
            {"rcept_no":"20241114002642","bsns_year":"2024","corp_code":"00126380","stock_code":"005930","reprt_code":"11014","account_nm":"자산총계","fs_div":"CFS","thstrm_amount":"1"}]}
            """;
        var line = Assert.Single(DartClient.ParseKeyAccounts(json));
        Assert.Equal(("OperatingIncome", 9_183_371_000_000m, 26_233_258_000_000m, new DateOnly(2024, 11, 14)),
            (line.Account, line.ThisAmount!.Value, line.ThisCumulative!.Value, line.ReceiptDate));
    }
}
