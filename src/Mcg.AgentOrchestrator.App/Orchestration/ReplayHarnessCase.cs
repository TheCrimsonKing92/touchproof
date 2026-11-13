namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReplayHarnessCase
{
    private ReplayHarnessCase(string caseId, DateTimeOffset recordedAt, string? recordedDecision,
        string? candidateDecision, string? notReplayableReason)
    {
        CaseId = caseId;
        RecordedAt = recordedAt;
        RecordedDecision = recordedDecision;
        CandidateDecision = candidateDecision;
        NotReplayableReason = notReplayableReason;
    }

    internal string CaseId { get; }
    internal DateTimeOffset RecordedAt { get; }
    internal string? RecordedDecision { get; }
    internal string? CandidateDecision { get; init; }
    internal bool Replayable => NotReplayableReason is null;
    internal string? NotReplayableReason { get; }

    internal static ReplayHarnessCase CreateReplayable(string caseId, DateTimeOffset recordedAt,
        string? recordedDecision, string? candidateDecision = null) =>
        new(caseId, recordedAt, recordedDecision, candidateDecision, null);

    internal static ReplayHarnessCase CreateNonReplayable(string caseId, DateTimeOffset recordedAt,
        string reason, string? recordedDecision = null, string? candidateDecision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(caseId, recordedAt, recordedDecision, candidateDecision, reason);
    }
}
