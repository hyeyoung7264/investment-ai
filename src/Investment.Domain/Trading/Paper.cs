namespace Investment.Domain.Trading;

public enum PaperSessionStatus
{
    Active = 0,
    Stopped = 1,
    Disabled = 2,
}

/// <summary>
/// A forward paper-trading session for one validated strategy version. No real orders are ever sent.
/// Paper sessions only process sessions on/after their start date — never a historical backfill.
/// </summary>
public sealed class PaperSession
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string StrategyId { get; set; }
    public Guid StrategyVersionId { get; set; }
    public required string ParametersJson { get; set; }
    public PaperSessionStatus Status { get; set; }
    public DateOnly StartDate { get; set; }
    public decimal InitialCapital { get; set; }
    public required string UniverseJson { get; set; }
    public required string CostModelJson { get; set; }
    public required string RiskLimitsJson { get; set; }
    public required string StateJson { get; set; }
    public DateOnly? LastProcessedDate { get; set; }

    /// <summary>Expected net return per trade from the validation evidence (for expected-vs-actual tracking).</summary>
    public double ExpectedEvPerTrade { get; set; }

    /// <summary>Regimes in which new entries are paused (negative or insufficient validated evidence).</summary>
    public required string BlockedRegimesJson { get; set; }

    public Guid? EvidenceEvaluationId { get; set; }
    public string? StoppedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An order decided at a signal close, with what was known at that time. Immutable once written, except fill status.</summary>
public sealed class PaperOrder
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public required string Ticker { get; set; }
    public required string Side { get; set; }
    public long Quantity { get; set; }
    public DateOnly SignalDate { get; set; }
    public decimal SignalPrice { get; set; }
    public required string Reason { get; set; }
    public double Score { get; set; }
    public string? MarketCondition { get; set; }
    public double ExpectedReturn { get; set; }
    public required string Status { get; set; }
    public DateOnly? FillDate { get; set; }
    public decimal? FillPrice { get; set; }
    public long? FilledQuantity { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A closed paper trade with its decision context (the fields the project brief requires).</summary>
public sealed class PaperTrade
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public required string StrategyId { get; set; }
    public required string Ticker { get; set; }
    public DateOnly SignalTime { get; set; }
    public decimal SignalPrice { get; set; }
    public DateOnly EntryTime { get; set; }
    public decimal EntryPrice { get; set; }
    public DateOnly ExitTime { get; set; }
    public decimal ExitPrice { get; set; }
    public long Quantity { get; set; }
    public double ExpectedReturn { get; set; }
    public double ActualReturn { get; set; }
    public double GrossReturn { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string? MarketCondition { get; set; }
    public required string Reason { get; set; }
    public required string ExitReason { get; set; }
    public required string Result { get; set; }
    public decimal Costs { get; set; }
    public decimal NetPnl { get; set; }
    public int HoldingSessions { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class PaperEquityPoint
{
    public Guid SessionId { get; set; }
    public DateOnly Date { get; set; }
    public decimal NetEquity { get; set; }
    public decimal GrossEquity { get; set; }
    public decimal Cash { get; set; }
    public int Positions { get; set; }
    public string? Regime { get; set; }
}

/// <summary>Audit log of each daily paper run (input data hash, code version).</summary>
public sealed class PaperRunLog
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset RunAt { get; set; }
    public DateOnly? ProcessedFrom { get; set; }
    public DateOnly? ProcessedTo { get; set; }
    public int SessionsProcessed { get; set; }
    public required string DataHash { get; set; }
    public required string CodeCommit { get; set; }
    public string? Notes { get; set; }
}
