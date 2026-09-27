namespace Investment.Domain.Research;

/// <summary>
/// Lifecycle of a strategy. Transitions are decided by the promotion gate from stored evidence,
/// never by assertion. REJECTED/DISABLED records are kept forever.
/// </summary>
public enum StrategyStatus
{
    Experimental = 0,
    Backtested = 1,
    Validated = 2,
    Paper = 3,
    Approved = 4,
    Rejected = 8,
    Disabled = 9,
}

public sealed class StrategyDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Family { get; set; }
    public required string Hypothesis { get; set; }
    public StrategyStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<StrategyVersion> Versions { get; set; } = [];
}

/// <summary>
/// Immutable (logic version, parameters) pair. Changing either creates a new version;
/// earlier results stay attached to their original version.
/// </summary>
public sealed class StrategyVersion
{
    public Guid Id { get; set; }
    public required string StrategyId { get; set; }
    public int Version { get; set; }
    public int LogicVersion { get; set; }
    public required string ParametersJson { get; set; }
    public required string ParametersHash { get; set; }
    public StrategyStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public StrategyDefinition? Strategy { get; set; }
}

public enum CostBasis
{
    Gross = 0,
    Net = 1,
}

public sealed class BacktestRun
{
    public Guid Id { get; set; }
    public Guid StrategyVersionId { get; set; }
    public StrategyVersion? StrategyVersion { get; set; }

    /// <summary>e.g. "single", "wf-train", "wf-validation", "wf-oos".</summary>
    public required string RunKind { get; set; }
    public string? Label { get; set; }
    public Guid? ParentRunId { get; set; }
    public Guid? StudyId { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal InitialCapital { get; set; }

    public required string UniverseJson { get; set; }
    public required string UniverseHash { get; set; }
    public required string CostModelJson { get; set; }
    public required string RiskLimitsJson { get; set; }
    public required string DataSource { get; set; }
    public required string DataHash { get; set; }
    public required string CodeCommit { get; set; }
    public bool CodeDirty { get; set; }
    public required string EngineVersion { get; set; }
    public required string ResultHash { get; set; }
    public DateOnly? HaltedOn { get; set; }
    public string? HaltReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public double DurationMs { get; set; }

    public List<BacktestMetric> Metrics { get; set; } = [];
    public List<BacktestTrade> Trades { get; set; } = [];
    public List<BacktestEquityPoint> Equity { get; set; } = [];
}

public sealed class BacktestMetric
{
    public Guid RunId { get; set; }
    public CostBasis Basis { get; set; }

    public double TotalReturn { get; set; }
    public double Cagr { get; set; }
    public double AvgDailyReturn { get; set; }
    public double DailyReturnStdev { get; set; }
    public double WinRate { get; set; }
    public double ProfitFactor { get; set; }
    public double AvgProfit { get; set; }
    public double AvgLoss { get; set; }
    public double ExpectedValuePerTrade { get; set; }
    public double ExpectedValuePerTradeKrw { get; set; }
    public double ExpectedValueTStat { get; set; }
    public double MaxDrawdown { get; set; }
    public double Sharpe { get; set; }
    public double Sortino { get; set; }
    public int NumberOfTrades { get; set; }
    public double AnnualTurnover { get; set; }
    public double AvgHoldingSessions { get; set; }
    public double Exposure { get; set; }
    public double TotalCosts { get; set; }
    public double BenchmarkReturn { get; set; }
    public int TradingDays { get; set; }
}

public sealed class BacktestTrade
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public required string Ticker { get; set; }
    public DateOnly EntrySignalDate { get; set; }
    public DateOnly EntryDate { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryReferencePrice { get; set; }
    public DateOnly ExitDate { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal ExitReferencePrice { get; set; }
    public long Quantity { get; set; }
    public decimal GrossPnl { get; set; }
    public decimal Costs { get; set; }
    public decimal NetPnl { get; set; }
    public double GrossReturn { get; set; }
    public double NetReturn { get; set; }
    public int HoldingSessions { get; set; }
    public required string EntryReason { get; set; }
    public required string ExitReason { get; set; }
    public double EntryScore { get; set; }
}

public sealed class BacktestEquityPoint
{
    public Guid RunId { get; set; }
    public DateOnly Date { get; set; }
    public decimal NetEquity { get; set; }
    public decimal GrossEquity { get; set; }
    public decimal Cash { get; set; }
    public int Positions { get; set; }
}

public static class StrategyStatusRules
{
    private static readonly StrategyStatus[] Progress =
        [StrategyStatus.Approved, StrategyStatus.Paper, StrategyStatus.Validated, StrategyStatus.Backtested, StrategyStatus.Experimental];

    /// <summary>
    /// A strategy's status is its most advanced active version; only when no version is active is it
    /// Disabled (if any version was disabled) or Rejected.
    /// </summary>
    public static StrategyStatus Rollup(IEnumerable<StrategyStatus> versionStatuses)
    {
        var set = versionStatuses.ToHashSet();
        foreach (var s in Progress) if (set.Contains(s)) return s;
        return set.Contains(StrategyStatus.Disabled) ? StrategyStatus.Disabled : StrategyStatus.Rejected;
    }
}
