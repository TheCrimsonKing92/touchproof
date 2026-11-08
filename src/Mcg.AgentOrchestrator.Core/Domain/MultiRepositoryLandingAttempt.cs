namespace Mcg.AgentOrchestrator.Core;

/// <summary>Recovery boundary for mutations that precede the final tuple landing record.</summary>
public sealed record MultiRepositoryLandingAttempt(GoalId GoalId, string TupleCanonical,
    IReadOnlyList<RepositoryLandingStep> Steps)
{
    // Write-ahead repair state belongs to the failed attempt, not the acceptance receipt.
    public IReadOnlyDictionary<string, RepositoryLandingRepairProgress>? RepairProgress { get; init; }
}

/// <summary>Prepared history is durable before publication, so interruption cannot double-revert.</summary>
public sealed record RepositoryLandingRepairProgress(string StartingTip, string IntegrationTip,
    string WorktreePath, string? RebuiltTip = null);
