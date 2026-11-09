using Mcg.AgentOrchestrator.Core;
using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceAllSkippedSelectionDecision(
    string? PolicyFailure, string? ApparatusDetail, IReadOnlyList<string> ZeroMatchSelections)
{
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
