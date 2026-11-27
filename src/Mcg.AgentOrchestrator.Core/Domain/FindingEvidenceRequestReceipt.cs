using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Core;

public sealed record FindingEvidenceRequestReceipt(
    string RequestGuard,
    string RerunGuard,
    string RequestingTaskId,
    string CandidateSha,
    string FindingRoundFingerprint,
    FindingEvidenceRequestReceiptDecision Decision,
    string? ReceiptId,
    string? OriginalReceiptId,
    FindingEvidenceRequestReceiptHold? Hold)
{
    public static string BuildRerunGuard(string requestGuard) => $"candidate-rerun:{requestGuard}";

    public bool Matches(TaskId requestingTaskId, string candidateSha, string fingerprint, string requestGuard) =>
        string.Equals(RequestingTaskId, requestingTaskId.Value, StringComparison.Ordinal) &&
        string.Equals(CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(FindingRoundFingerprint, fingerprint, StringComparison.Ordinal) &&
        string.Equals(RequestGuard, requestGuard, StringComparison.Ordinal);
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingEvidenceRequestReceiptDecision>))]
public enum FindingEvidenceRequestReceiptDecision
{
    BaselineArmAbsent,
    CandidateRerunRequested,
    CandidateRerunGreen,
    CandidateRerunRed,
    CandidateRerunUnusable
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingEvidenceRequestReceiptHold>))]
public enum FindingEvidenceRequestReceiptHold
{
    Escalation,
    UnconfirmedRedRouting
}
