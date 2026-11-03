namespace Mcg.AgentOrchestrator.Core;

// Criterionless acceptance evidence is candidate-bound, not a criterion obligation.
// The append-only timeline retains its carry and settlement across snapshot recovery.
internal static class AcceptanceOwnedFindingCarry
{
    private const string Carried = "ACCEPTANCE_FINDING_CARRIED ";
    private const string Closed = "ACCEPTANCE_FINDING_CLOSED ";

    internal static bool HasTypedEvidenceRequest(ReviewFinding finding) =>
        finding.EvidenceRequest is { Selections.Count: > 0 } request &&
        request.Selections.All(selection =>
            selection is not null && !string.IsNullOrWhiteSpace(selection.TestProject) &&
            !string.IsNullOrWhiteSpace(selection.TestClass));

    internal static bool IsCarriable(Goal goal, ReviewFinding finding, string? candidateSha) =>
        finding.Category == FindingCategory.AcceptanceOwned &&
        !string.IsNullOrWhiteSpace(finding.StableId) &&
        !string.IsNullOrWhiteSpace(candidateSha) &&
        HasTypedEvidenceRequest(finding) &&
        VerificationFindingEvidencePolicy.FindAuthoritativelyDeferredOffWorkerObligation(
            goal, finding, candidateSha) is null;

    private static string Identity(string stableId, string candidateSha) =>
        $"stable_id={Uri.EscapeDataString(stableId)}; candidate_sha={Uri.EscapeDataString(candidateSha.Trim().ToLowerInvariant())}";

    internal static string CarryNote(string stableId, string candidateSha) =>
        Carried + Identity(stableId, candidateSha);

    internal static string ClosureNote(string stableId, string candidateSha, string source) =>
        Closed + Identity(stableId, candidateSha) +
        $"; receipt=full-acceptance:{Uri.EscapeDataString(candidateSha.Trim().ToLowerInvariant())}; source={System.Text.Json.JsonSerializer.Serialize(source)}";

    internal static bool IsCarried(Goal goal, string stableId, string candidateSha) =>
        goal.Timeline.Any(item => item.Kind == ProgressKind.TaskNote && item.TaskId is not null &&
            string.Equals(item.Message, CarryNote(stableId, candidateSha), StringComparison.Ordinal));

    internal static IReadOnlyList<string> GetOpenCarried(Goal goal, string? candidateSha)
    {
        if (string.IsNullOrWhiteSpace(candidateSha)) return [];
        var open = new HashSet<string>(StringComparer.Ordinal);
        var suffix = "; candidate_sha=" + Uri.EscapeDataString(candidateSha.Trim().ToLowerInvariant());
        foreach (var item in goal.Timeline)
        {
            // Match the entire canonical carry note. Embedded worker descriptions
            // and delimiters inside ids cannot create ledger entries.
            if (item.Kind != ProgressKind.TaskNote || item.TaskId is null ||
                !item.Message.StartsWith(Carried + "stable_id=", StringComparison.Ordinal) ||
                !item.Message.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var encodedId = item.Message[(Carried.Length + "stable_id=".Length)..^suffix.Length];
            var stableId = Uri.UnescapeDataString(encodedId);
            if (stableId.Length > 0 && item.Message == CarryNote(stableId, candidateSha)) open.Add(stableId);
        }
        // Only goal-level gate notes can settle a carry; task/worker notes cannot.
        open.RemoveWhere(stableId => goal.Timeline.Any(item =>
            item.Kind == ProgressKind.TaskNote && item.TaskId is null &&
            item.Message.StartsWith(Closed + Identity(stableId, candidateSha) +
                $"; receipt=full-acceptance:{Uri.EscapeDataString(candidateSha.Trim().ToLowerInvariant())}; source=", StringComparison.Ordinal)));
        return open.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
}
