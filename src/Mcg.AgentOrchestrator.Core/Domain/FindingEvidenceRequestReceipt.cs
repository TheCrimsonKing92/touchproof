using System.Security.Cryptography;
using System.Text;
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

    internal static FindingEvidenceRequestReceipt ForReuse(
        TaskId requestingTaskId, FindingEvidenceReceipt reusedReceipt, string requestIdentity,
        string fingerprint, FindingEvidenceRequestReceiptDecision decision)
    {
        var guard = $"candidate_sha={reusedReceipt.CandidateSha}; finding_round={fingerprint}; request_identity={requestIdentity}";
        return new FindingEvidenceRequestReceipt(
            guard, BuildRerunGuard(guard), requestingTaskId.Value, reusedReceipt.CandidateSha, fingerprint, decision,
            reusedReceipt.ReceiptId, OriginalReceiptId: null, Hold: null);
    }

    internal static string BuildRoundFingerprint(
        TaskId requestingTaskId,
        DateTimeOffset? verificationCompletedAt,
        ReviewFindingRound round)
    {
        var verificationTicks = verificationCompletedAt?.ToUniversalTime().Ticks ?? 0;
        var findings = round.Findings
            .OrderBy(finding => finding.StableId, StringComparer.Ordinal)
            .Select(finding => string.Join(
                "\u001f",
                finding.StableId,
                finding.State,
                finding.Severity,
                finding.Category,
                finding.Location.File,
                finding.Location.Region,
                finding.Description,
                string.Join(
                    "\u001e",
                    (finding.EvidenceRequest?.Selections ?? [])
                        .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
                        .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
                        .Select(selection => $"{selection.TestProject}:{selection.TestClass}"))));
        var payload = $"{requestingTaskId.Value}\u001d{verificationTicks}\u001d{string.Join("\u001d", findings)}";
        return "finding-round-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant()[..24];
    }

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
    CandidateRerunUnusable,
    PendingExecutionGate,
    ReusedGreen,
    ReusedRed
}

[JsonConverter(typeof(JsonStringEnumConverter<FindingEvidenceRequestReceiptHold>))]
public enum FindingEvidenceRequestReceiptHold
{
    Escalation,
    UnconfirmedRedRouting,
    PendingExecutionGate
}
