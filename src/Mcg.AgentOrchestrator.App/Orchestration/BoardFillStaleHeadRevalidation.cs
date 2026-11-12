namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillStaleHeadRevalidation
{
    internal const string AdmittedReason = "ok:revalidated-main-moved";

    internal static bool CanAdmit(BoardFillDraftRound draft, string currentHead,
        Func<string, string, IReadOnlyList<string>?> changedPaths)
    {
        if (string.IsNullOrWhiteSpace(draft.MainHead) || draft.Assessment is null) return false;
        var cited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var verdict in draft.Assessment.Verification.Verdicts)
        {
            if (string.IsNullOrWhiteSpace(verdict.Evidence)) continue;
            var citation = BoardFillCitation.Parse(verdict.Evidence.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(citation.Path)) return false;
            cited.Add(citation.Path);
        }
        if (cited.Count == 0) return false;
        var changed = changedPaths(draft.MainHead, currentHead);
        // Partial reads cannot prove that every cited path was left alone.
        return changed is { Count: > 0 } && changed.All(path =>
            !string.IsNullOrWhiteSpace(path) && !cited.Contains(path.Replace('\\', '/')));
    }
}
