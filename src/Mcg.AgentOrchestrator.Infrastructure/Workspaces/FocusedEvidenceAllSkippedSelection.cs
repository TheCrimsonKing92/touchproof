namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record FocusedEvidenceAllSkippedSelection(string Selection, IReadOnlyList<string> Reasons)
{
    internal const string Reason = "all-skipped";

    internal static string FormatSummary(IEnumerable<AcceptanceCheckResult> checks)
    {
        var selections = checks.SelectMany(check => check.AllSkippedSelections ?? [])
            .DistinctBy(item => item.Selection, StringComparer.Ordinal).ToArray();
        return selections.Length == 0 ? string.Empty :
            "; all-skipped: " + string.Join("; ", selections.Select(item =>
                $"{item.Selection} ({string.Join(',', item.Reasons)})"));
    }
}
