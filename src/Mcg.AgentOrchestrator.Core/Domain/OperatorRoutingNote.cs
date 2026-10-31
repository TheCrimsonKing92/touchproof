namespace Mcg.AgentOrchestrator.Core;

public sealed record OperatorRoutingNote(
    string IntentId,
    string Text,
    IReadOnlyList<string> EvidenceReferences,
    RetryCause Cause,
    DateTimeOffset AcceptedAt,
    bool QueuedWhileRunning,
    DateTimeOffset? TargetRoundRetryAt = null,
    DateTimeOffset? DeliveredAt = null,
    DateTimeOffset? SubmittedAt = null)
{
    public const string QueuedOutcomeToken = "operator-routing-note-queued";

    // This suffix is issued by the state owner only after the note is durable.
    public const string QueuedOutcome = "Operator note queued for the next round. [" + QueuedOutcomeToken + "]";

    public static bool IsQueuedOutcome(string? outcome) =>
        outcome?.EndsWith(QueuedOutcome, StringComparison.Ordinal) == true;
}
