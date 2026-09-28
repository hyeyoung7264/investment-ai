namespace Investment.Domain.Market;

/// <summary>
/// One key account of one periodic report as filed on DART (fnlttMultiAcnt). Amounts in KRW.
/// ReceiptDate comes from the receipt number — the day the figures became public in this report.
/// </summary>
public sealed class FinancialReportLine
{
    public required string CorpCode { get; set; }
    public string? Ticker { get; set; }
    public int FiscalYear { get; set; }

    /// <summary>11013 = Q1, 11012 = half-year, 11014 = Q3, 11011 = annual.</summary>
    public required string ReportCode { get; set; }

    /// <summary>CFS (consolidated) or OFS (separate).</summary>
    public required string FsDiv { get; set; }

    /// <summary>Revenue, OperatingIncome or NetIncome.</summary>
    public required string Account { get; set; }

    public decimal? ThisAmount { get; set; }
    public decimal? ThisCumulative { get; set; }
    public decimal? PriorAmount { get; set; }
    public decimal? PriorCumulative { get; set; }
    public required string ReceiptNo { get; set; }
    public DateOnly ReceiptDate { get; set; }
}

/// <summary>A company's fiscal quarter as known on <see cref="ReceiptDate"/> (Q4 derived as annual − 9-month).</summary>
public sealed record QuarterResult(
    string Ticker, int FiscalYear, int Quarter, DateOnly ReceiptDate, string FsDiv,
    decimal? Revenue, decimal? OperatingIncome, decimal? PriorOperatingIncome, decimal? NetIncome);

/// <summary>Per-ticker fundamentals for a simulation: filed quarters (by receipt date) and official daily market cap.</summary>
public sealed record TickerFundamentals(QuarterResult[] Quarters, DateOnly[] CapDates, double[] MarketCaps);

/// <summary>Fundamentals as known at the as-of date.</summary>
public readonly record struct FundamentalSnapshot(double? TtmOperatingIncome, double? TtmRevenue, double? MarketCap, int QuartersKnown);
