namespace Investment.Domain.Market;

public enum DisclosureEvent
{
    Other = 0,
    Buyback = 1,
    BuybackTrust = 2,
    TreasuryDisposal = 3,
    RightsOffering = 10,
    ConvertibleBond = 11,
    BondWithWarrant = 12,
    ExchangeableBond = 13,
    BonusIssue = 20,
    CapitalReduction = 21,
    SupplyContract = 30,
    PreliminaryEarnings = 31,
    EarningsChange = 32,
    Merger = 40,
    Split = 41,
    LargestShareholderChange = 42,
}

/// <summary>
/// One DART filing. Only the receipt date is known (no time), so the event is usable at the earliest for a
/// decision made after that day's filing window — i.e. entries at the next session's open.
/// </summary>
public sealed class Disclosure
{
    public required string ReceiptNo { get; set; }
    public required string CorpCode { get; set; }
    public required string CorpName { get; set; }
    public string? Ticker { get; set; }
    public string? CorpClass { get; set; }
    public required string ReportName { get; set; }
    public DateOnly ReceiptDate { get; set; }
    public string? Filer { get; set; }
    public string? Remark { get; set; }
    public required string DisclosureType { get; set; }
    public DisclosureEvent Event { get; set; }
    public bool IsCorrection { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
}

/// <summary>Point-in-time event as strategies see it.</summary>
public readonly record struct CorporateEvent(DateOnly Date, DisclosureEvent Type, string Title);
