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
    Etf = 5,
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

/// <summary>
/// A verified share-count event (split / reverse split) found by comparing the source's adjusted prices
/// with raw prices. When the source left pre-event volume unadjusted, <see cref="VolumeCorrection"/> is the
/// factor that pre-event volume must be multiplied by so that adjusted close × volume ≈ raw trading value.
/// </summary>
public sealed class SplitEvent
{
    public required string Ticker { get; set; }
    public DateOnly EventDate { get; set; }

    /// <summary>(adjusted/raw before) ÷ (adjusted/raw after). 0.2 = 5-for-1 split, 10 = 1-for-10 reverse split.</summary>
    public double PriceFactor { get; set; }

    public bool SourceVolumeAdjusted { get; set; }
    public double VolumeCorrection { get; set; } = 1;
    public DateTimeOffset CheckedAt { get; set; }
    public string? Notes { get; set; }
}
