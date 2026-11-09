using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateTestObligationAllSkippedGrade
{
    internal const string Reason = "all-skipped";

    internal static GateTestObligationEvaluator.Result Grade(
        GateTestObligation obligation, GateTestObligationEvaluator.Source source,
        IReadOnlyList<GroupedGateReceiptTrxEvidence.SelectorEvidence> evidence,
        string orchestratorDirectory, bool declaredApplies)
    {
        var original = GateTestObligationEvaluator.Grade(obligation, source.Id, evidence);
        if (declaredApplies || original.Outcome == GateTestObligationEvaluator.Outcome.Satisfied) return original;

        var unsatisfied = evidence.Where(item => !item.Satisfied).ToArray();
        if (unsatisfied.Any(item => item.Passed != 0 || item.Superseded != 0 || item.Failed <= 0 ||
            item.AmbiguousClassNames is not null ||
            GateCheckSelectorMatcher.Grade(item.Selector, source.Checks) != GateCheckSelectorMatcher.Outcome.NotApplicable))
            return original;

        ProspectiveEvidenceTrxRows.Row[] rows;
        try
        {
            rows = source.Paths.Select(path => Path.GetFullPath(path, orchestratorDirectory))
                .SelectMany(ProspectiveEvidenceTrxRows.Read).ToArray();
        }
        catch (Exception)
        {
            // Incomplete or unreadable evidence cannot authorize a nonblocking outcome.
            return original;
        }

        // Grouped evidence conservatively counts skipped-only rows as failed. Prove
        // their exact outcome without turning any skipped row into a passing result.
        foreach (var item in unsatisfied)
        {
            var matches = ProspectiveEvidenceTrxRows.Match(item.Selector, rows);
            if (matches.Length == 0 || matches.Length != item.Failed || matches.Any(row => row.Outcome != "NotExecuted"))
                return original;
        }

        return new(obligation, GateTestObligationEvaluator.Outcome.AllSkipped, obligation.Selectors, source.Id,
            AllSkippedSelectors: unsatisfied.Select(item => item.Selector).ToArray());
    }

    internal static string Provenance(GateTestObligationEvaluator.Result result)
    {
        if (result.AllSkippedSelectors is not { Count: > 0 } selectors) return "";
        var prefix = $"GATE_TEST_OBLIGATION_ALL_SKIPPED id={result.Obligation.Id[..Math.Min(8, result.Obligation.Id.Length)]}";
        var entries = selectors.Select(selector =>
            $"{prefix} selector={selector} reason={Reason} source={result.SourceId}");
        if (selectors.Count == result.Obligation.Selectors.Count)
            entries = entries.Append($"{prefix} scope=every-selector selectors={selectors.Count} reason={Reason} source={result.SourceId}");
        return string.Join("; ", entries);
    }
}
