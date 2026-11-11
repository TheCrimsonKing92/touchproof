namespace Mcg.AgentOrchestrator.Core;

public sealed record ProgressEvent(
    GoalId GoalId,
    TaskId? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt,
    TaskRequeueSkippedPayload? RequeueSkipped = null,
    IReadOnlyList<OperatorGateRecord>? OperatorGates = null,
    OperatorIntentAppliedPayload? OperatorIntentApplied = null,
    HumanInputSupersededPayload? HumanInputSuperseded = null,
    ConductorTickOutcomePayload? TickOutcome = null,
    ProgressFailurePayload? Failure = null,
    DateTimeOffset? OperatorIntentCreatedAt = null);

// Facts captured when a failure is recorded, before its diagnostic text is composed.
public sealed record ProgressFailurePayload(string Kind, string? Rule, IReadOnlyList<ProgressFailureEvidence> Evidence)
{
    public bool Equals(ProgressFailurePayload? other) => other is not null &&
        Kind == other.Kind && Rule == other.Rule && Evidence.SequenceEqual(other.Evidence);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Kind); hash.Add(Rule);
        foreach (var entry in Evidence) hash.Add(entry);
        return hash.ToHashCode();
    }
}

public sealed record ProgressFailureEvidence(string Name, string Value, string? NextStep = null);

public sealed record ConductorTickOutcomePayload(
    string OutcomeKind,
    string LifecycleState,
    string? EscalationKind,
    PolicyDecisionRecord? Decision = null);

public sealed record PolicyDecisionFact(string Name, string Value);

public sealed record PolicyDecisionRecord(
    string Stage,
    string Action,
    int Rung,
    string DiscriminatingEvidence,
    string Reason,
    IReadOnlyList<PolicyDecisionFact> Facts);

public sealed record HumanInputSupersededPayload(
    IReadOnlyList<string> ResolvedStableIds);

public sealed record OperatorIntentAppliedPayload(
    string IntentId,
    string Verb,
    string? TaskId,
    string Actor,
    string Channel,
    string? AuthenticationAssurance,
    OperatorActorKind ActorKind = OperatorActorKind.Human,
    string? DecisionId = null,
    string? Outcome = null,
    RetryRoundKind? RetryRoundKind = null,
    OperatorIntentBlocker? BlockedBy = null);

// The obstruction observed at application time, retained even after it clears.
public sealed record OperatorIntentBlocker(
    string TaskId,
    AgentRole Role,
    string Reason,
    int TaskNumber,
    DateTimeOffset? FailureOccurredAt,
    DateTimeOffset? LatestRetryAt);

public sealed record TaskRequeueSkippedPayload(
    string TaskId,
    string GoalId,
    string DispatchId,
    string BlockingEntity,
    string? TerminalState,
    string Reason,
    string? Detail = null);
