namespace Mcg.AgentOrchestrator.Core;

/// <summary>Recovery boundary for mutations that precede the final tuple landing record.</summary>
public sealed record MultiRepositoryLandingAttempt(GoalId GoalId, string TupleCanonical,
    IReadOnlyList<RepositoryLandingStep> Steps);
