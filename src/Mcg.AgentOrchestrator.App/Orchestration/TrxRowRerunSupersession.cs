namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TrxRowRerunSupersession
{
    internal static int Count(
        IReadOnlyList<ProspectiveEvidenceTrxRows.Row> matches,
        IReadOnlyList<ProspectiveEvidenceTrxRows.Row> rows) =>
        matches.Count(row => !row.Passed && rows.Any(rerun => rerun.Passed &&
            string.Equals(rerun.FullIdentity, row.FullIdentity, StringComparison.Ordinal) &&
            !string.Equals(rerun.Path, row.Path, StringComparison.OrdinalIgnoreCase)));
}
