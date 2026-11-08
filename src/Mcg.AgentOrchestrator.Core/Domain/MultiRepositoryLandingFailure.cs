namespace Mcg.AgentOrchestrator.Core;

/// <summary>An operator-owned failed tuple attempt, including all successful repairs.</summary>
public sealed record MultiRepositoryLandingFailure(GoalId GoalId, string TupleCanonical, string FailingKey,
    string Error, IReadOnlyDictionary<string, IReadOnlyList<string>> CompensatingCommits,
    IReadOnlyList<string> ManualRepairKeys, DateTimeOffset RecordedAt);
