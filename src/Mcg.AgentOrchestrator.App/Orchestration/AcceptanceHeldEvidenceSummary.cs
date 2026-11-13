namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceHeldEvidenceRow(
    string Action, string Reason, int DecisionCount, long Bytes, int Unmeasured);

// Counts decision records independently, including overlapping or repeated held paths.
internal sealed record AcceptanceHeldEvidenceSummary(IReadOnlyList<AcceptanceHeldEvidenceRow> Rows)
{
    internal static AcceptanceHeldEvidenceSummary FromDecisions(IEnumerable<EvidenceRetentionDecision> decisions)
    {
        var rows = decisions.Where(decision => decision.Family == EvidenceArtifactFamily.AcceptanceGateAttempts &&
                decision.Action != EvidenceRetentionAction.Deleted)
            .GroupBy(decision => (Action: decision.Action.ToString(), decision.Reason))
            .Select(group =>
            {
                long bytes = 0;
                var unmeasured = 0;
                var count = 0;
                foreach (var decision in group)
                {
                    count++;
                    if (TryMeasure(decision.Path, out var measured)) bytes += measured;
                    else unmeasured++;
                }
                return new AcceptanceHeldEvidenceRow(group.Key.Action, group.Key.Reason, count, bytes, unmeasured);
            }).OrderBy(row => row.Action, StringComparer.Ordinal)
            .ThenBy(row => row.Reason, StringComparer.Ordinal).ToArray();
        return new(rows);
    }

    private static bool TryMeasure(string path, out long bytes)
    {
        bytes = 0;
        try
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out var current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.GetFileSystemEntries(current, "*", new EnumerationOptions
                    { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 }))
                        pending.Push(child);
                }
                else total = checked(total + new FileInfo(current).Length);
            }
            bytes = total;
            return true;
        }
        // A failed or partial measurement contributes no bytes and cannot break snapshot publication.
        catch (Exception) { return false; }
    }
}
