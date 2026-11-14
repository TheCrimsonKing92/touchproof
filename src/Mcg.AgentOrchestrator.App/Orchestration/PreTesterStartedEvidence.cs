using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A started run owns its recorded identity and obligations until a terminal entry replaces it.
internal static class PreTesterStartedEvidence
{
    internal const string TruncatedStoredMessage = nameof(TruncatedStoredMessage);
    internal const string MalformedSelectionName = nameof(MalformedSelectionName);

    // Notices belong to the receipt for the conductor process lifetime, across driver instances.
    private static readonly ConcurrentDictionary<string, byte> DriftNotices = new(StringComparer.Ordinal);

    internal static Action<string> DriftProgress(string receiptId) => line =>
    {
        if (DriftNotices.TryAdd(receiptId, 0)) Console.WriteLine(line);
    };

    internal static bool TryAbandon(PreTesterEvidenceEntry? prior, string reason, Action<PreTesterEvidenceEntry, string> record)
    {
        if (prior?.Outcome != "started") return false;
        var abandoned = prior with { Outcome = "abandoned", ResultPath = null, FailingTests = [], AbandonmentReason = ReasonToken(reason) };
        record(abandoned, PreTesterEvidenceIndexLines.FormatMarker(abandoned));
        return true;
    }

    internal static bool HasAbandonmentReason(string message, string reason) =>
        message.EndsWith(AbandonmentSuffix(reason), StringComparison.Ordinal);

    internal static void RecordStarted(string candidateSha, string receiptId, IReadOnlyList<string> selections,
        IReadOnlyList<string> notRun, Action<PreTesterEvidenceEntry, string> record)
    {
        var entry = new PreTesterEvidenceEntry("started", candidateSha, receiptId, selections, notRun, null, []);
        record(entry, PreTesterEvidenceIndexLines.FormatMarker(entry));
    }

    private static string AbandonmentSuffix(string reason) =>
        "; abandoned_reason=" + ReasonToken(reason);

    internal static string ReasonToken(string reason) =>
        Regex.Replace(reason, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
}

internal sealed record PreTesterEvidenceRecorder(
    Action<GoalId, TaskId, PreTesterEvidenceEntry, string> Request,
    Action<GoalId, TaskId, PreTesterEvidenceEntry, string> Run)
{
    internal static PreTesterEvidenceRecorder FromLegacy(Action<GoalId, TaskId, string> request,
        Action<GoalId, TaskId, string> run) => new(
            (gid, tid, _, message) => request(gid, tid, message),
            (gid, tid, _, message) => run(gid, tid, message));
}
