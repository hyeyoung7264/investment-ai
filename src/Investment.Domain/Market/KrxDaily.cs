namespace Investment.Domain.Market;

/// <summary>
/// Official KRX daily record (Open API, unadjusted). Source of actual trading value, market cap and listed shares;
/// also used to cross-check the adjusted price source.
/// </summary>
public sealed class KrxDaily
{
    public required string Ticker { get; set; }
    public DateOnly Date { get; set; }
    public required string Market { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }

    /// <summary>Change vs the previous day's (corporate-action adjusted) base price: Close − ChangeFromPrevious = base.</summary>
    public decimal ChangeFromPrevious { get; set; }
    public long Volume { get; set; }
    public decimal TradingValue { get; set; }
    public decimal MarketCap { get; set; }
    public long ListedShares { get; set; }
}
