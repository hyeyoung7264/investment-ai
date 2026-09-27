namespace Investment.Domain.Research;

/// <summary>A pre-declared test of a hypothesis (e.g. one walk-forward study). All variants tried are recorded.</summary>
public sealed class ResearchStudy
{
    public Guid Id { get; set; }
    public required string StrategyId { get; set; }
    public required string Kind { get; set; }
    public required string Hypothesis { get; set; }
    public required string PlanJson { get; set; }
    public int VariantsTried { get; set; }
    public string? SummaryJson { get; set; }
    public required string CodeCommit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum GateDecision
{
    Promote,
    Hold,
    Reject,
    Disable,
}

/// <summary>Evidence → Evaluation → Decision. Every gate evaluation is stored, including holds.</summary>
public sealed class StrategyEvaluation
{
    public Guid Id { get; set; }
    public Guid StrategyVersionId { get; set; }
    public Guid? StudyId { get; set; }
    public required string Stage { get; set; }
    public StrategyStatus FromStatus { get; set; }
    public StrategyStatus ToStatus { get; set; }
    public GateDecision Decision { get; set; }
    public required string EvidenceJson { get; set; }
    public required string CriteriaJson { get; set; }
    public required string Reasons { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
}

/// <summary>Failed experiments are kept so the same idea is not retried blindly.</summary>
public sealed class ExperimentFailure
{
    public Guid Id { get; set; }
    public required string StrategyId { get; set; }
    public Guid StrategyVersionId { get; set; }
    public Guid EvaluationId { get; set; }
    public required string Hypothesis { get; set; }
    public required string ParametersJson { get; set; }
    public DateOnly TestFrom { get; set; }
    public DateOnly TestTo { get; set; }
    public required string ResultJson { get; set; }
    public required string FailureReason { get; set; }
    public string? RegimeNotes { get; set; }
    public DateTimeOffset RejectedAt { get; set; }
}
