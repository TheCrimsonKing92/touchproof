using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceExecutedCoverage
{
    // A zero-match Candidate arm can have no omissions. Receipts predating arm data
    // keep their existing coverage semantics.
    internal static bool CanClaimSelectionAuthority(FindingEvidenceReceipt receipt) =>
        receipt.Arms is not { Count: > 0 } arms ||
        arms.Any(arm => arm is { Arm: FindingEvidenceArm.Candidate, ExecutedTestCount: > 0 });

    internal static IReadOnlyList<FindingEvidenceSelection> ExecutedSelections(
        FindingEvidenceReceipt receipt, Func<FindingEvidenceSelection, string> format)
    {
        var notRun = (receipt.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.Candidate)
            .SelectMany(arm => FindingEvidenceRoundPlanner.ExpandSelections(arm.NotRunSelections))
            .Select(format).ToHashSet(StringComparer.Ordinal);
        // Expand class unions without reordering the receipt's requested selections.
        return receipt.Request.Selections
            .SelectMany(selection => FindingEvidenceRoundPlanner.ExpandSelections([selection])).Distinct()
            .Where(selection => !notRun.Contains(format(selection))).ToArray();
    }

    internal static FindingEvidenceRequest ExecutedRequest(FindingEvidenceReceipt receipt, Func<FindingEvidenceSelection, string> format) =>
        receipt.Request with { Selections = ExecutedSelections(receipt, format) };

    internal static IReadOnlyList<string> ExecutedSelections(IReadOnlyList<string> selections, IReadOnlyList<string> notRun)
    {
        var excluded = notRun.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selections.Where(selection => !excluded.Contains(selection) &&
            !excluded.Contains(selection.Split(':', 2)[^1])).ToArray();
    }
}
