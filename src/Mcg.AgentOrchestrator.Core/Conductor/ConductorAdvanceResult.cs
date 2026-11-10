using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Conductor;

public enum ConductorEscalationKind
{
    Unspecified,
    AcceptanceVerificationFailed,
    BackgroundAcceptanceFailed
}

public enum ConductorHoldOwner
{
    None,
    BackgroundAttempt,
    AcceptanceQueue,
    DurableOutbox,
    WorkerCapacity,
    Environment,
    PullRequestReview,
    Operator
}

public abstract record ConductorAdvanceOutcome
{
    public sealed record Executed(GoalLifecycleState FromState, string Description) : ConductorAdvanceOutcome
    {
        public PolicyDecisionRecord? Decision { get; init; }
    }
    public sealed record Held(GoalLifecycleState State, string Reason, string? StableIdentity = null) : ConductorAdvanceOutcome
    {
        public UnchangedCandidateHoldReason? TypedReason { get; init; }
        public PlannerBriefAmendmentRevisionPendingHold? AmendmentRevisionPending { get; init; }
        public PlannerOperatorEvidenceConsistencyHold? OperatorEvidenceConsistency { get; init; }
        public ConductorHoldOwner Owner { get; init; } = ConductorHoldOwner.None;
        public PolicyDecisionRecord? Decision { get; init; }
    }
    public sealed record Escalated(GoalLifecycleState State, string Reason, ConductorEscalationKind? Kind = null) : ConductorAdvanceOutcome
    {
        public PolicyDecisionRecord? Decision { get; init; }
        public ProgressFailurePayload? Failure { get; init; }
    }
    public sealed record Done(GoalLifecycleState State) : ConductorAdvanceOutcome
    {
        public PolicyDecisionRecord? Decision { get; init; }
    }
}

public sealed record ConductorAdvanceResult(
    string GoalId,
    string GoalPrefix,
    string PolicyName,
    ConductorAdvanceOutcome Outcome)
{
    public bool WasEscalated => Outcome is ConductorAdvanceOutcome.Escalated;
    public bool WasExecuted => Outcome is ConductorAdvanceOutcome.Executed;
    public bool IsDone => Outcome is ConductorAdvanceOutcome.Done;
    public bool IsHeld => Outcome is ConductorAdvanceOutcome.Held;
}
