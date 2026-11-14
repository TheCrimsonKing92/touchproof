using Mcg.AgentOrchestrator.Core;
using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceAllSkippedSelectionDecision(
    string? PolicyFailure, string? ApparatusDetail, IReadOnlyList<string> ZeroMatchSelections)
{
    // The persisted check carries the decision as OutputTail, not as structured coverage.
    // Keep this reader beside the writer so consumers can retain names even when their
    // request/coverage mapping is absent or no longer matches the failed check.
    internal static IReadOnlyList<string> ReadZeroMatchSelections(string? detail)
    {
        const string marker = " had selection(s) matching 0 tests: ";
        if (detail is null) return [];
        var start = detail.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return [];
        var line = detail[(start + marker.Length)..].Split(['\r', '\n'], 2)[0].Trim();
        if (!line.EndsWith(".", StringComparison.Ordinal)) return [];
        return line[..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name.StartsWith("FullyQualifiedName~", StringComparison.Ordinal))
            .Select(name => name["FullyQualifiedName~".Length..])
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static FocusedEvidenceAllSkippedSelectionDecision Decide(
        string checkName, bool isFocusedEvidenceSelection, int? executedTestCount,
        FocusedEvidenceSelectionCoverage coverage)
    {
        var zeroMatchSelections = coverage.UncoveredSelections.Except(
            (coverage.AllSkippedSelections ?? []).Select(item => item.Selection), StringComparer.Ordinal).ToArray();
        if (!isFocusedEvidenceSelection)
            return new(null, null, zeroMatchSelections);
        if (coverage.UnreadableReceiptPaths.Count > 0)
            return new(AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable,
                $"'{checkName}' could not read test receipt(s): {string.Join(", ", coverage.UnreadableReceiptPaths)}.",
                zeroMatchSelections);
        if (executedTestCount == 0)
            return new(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                $"'{checkName}' executed 0 tests.", zeroMatchSelections);
        if (zeroMatchSelections.Length > 0)
            return new(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
                $"'{checkName}' had selection(s) matching 0 tests: {string.Join(", ", zeroMatchSelections)}.",
                zeroMatchSelections);
        return new(null, null, zeroMatchSelections);
    }
}
