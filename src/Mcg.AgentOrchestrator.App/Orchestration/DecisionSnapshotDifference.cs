namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record DecisionSnapshotDifference(
    string GoalId,
    string DecisionKind,
    DecisionSnapshotGoal? Adopted,
    DecisionSnapshotGoal? Candidate,
    bool IsRegression,
    bool Exempted);
