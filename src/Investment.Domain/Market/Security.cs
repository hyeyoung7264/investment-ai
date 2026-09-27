namespace Investment.Domain.Market;

public enum MarketType
{
    Unknown = 0,
    Kospi = 1,
    Kosdaq = 2,
    Konex = 3,
}

public enum SecurityKind
{
    Common = 0,
    Preferred = 1,
    Spac = 2,
    Reit = 3,
    Fund = 4,
    Other = 9,
}

/// <summary>
/// A listed (or formerly listed) equity. Delisted securities are kept so that
/// point-in-time universes do not suffer from survivorship bias.
/// </summary>
public sealed class Security
{
    public required string Ticker { get; set; }
    public required string Name { get; set; }
    public MarketType Market { get; set; }
    public SecurityKind Kind { get; set; }
    public string? Sector { get; set; }
    public DateOnly? ListedDate { get; set; }
    public DateOnly? DelistedDate { get; set; }
    public string? DelistingReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsDelisted => DelistedDate is not null;
}

/// <summary>
/// One trading day of a security as stored. Prices are split-adjusted (source: Naver);
/// volume is NOT adjusted by the source, so <see cref="TradingValueEstimate"/> =
/// adjusted close × raw volume understates pre-split value. See docs/DATA-QUALITY.md.
/// </summary>
public sealed class DailyPrice
{
    public required string Ticker { get; set; }
    public DateOnly Date { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal TradingValueEstimate { get; set; }

    /// <summary>No trades that day (source reports O=H=L=0 or volume 0). Not fillable.</summary>
    public bool IsHalted { get; set; }

    public required string Source { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
}

public sealed class IndexPrice
{
    public required string IndexCode { get; set; }
    public DateOnly Date { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
}

public sealed class IngestionRun
{
    public Guid Id { get; set; }
    public required string Source { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int ItemsRequested { get; set; }
    public int ItemsFailed { get; set; }
    public long RowsUpserted { get; set; }
    public required string Status { get; set; }
    public string? Notes { get; set; }
}
