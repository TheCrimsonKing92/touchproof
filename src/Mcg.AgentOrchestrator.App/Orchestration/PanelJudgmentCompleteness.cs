namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PanelJudgmentOutcome { Complete, Partial, Skipped }

// Settlement is operational. Only two valid, distinct-provider receipts establish a judgment.
internal sealed record PanelJudgmentCompleteness(PanelJudgmentOutcome Outcome, string? Reason)
{
    internal static PanelJudgmentCompleteness Evaluate(IReadOnlyList<PanelJudgeResult> results)
    {
        var valid = results.Where(result => result.Outcome == PanelJudgeOutcome.Valid).ToArray();
        if (results.Count == 2 && valid.Length == 2 &&
            valid.Select(result => result.Judge).Distinct(StringComparer.Ordinal).Count() == 2 &&
            valid.All(result => ProviderBoundPanelJudgeBinding.Supported(result.WorkerProfileName)) &&
            valid.Select(result => result.WorkerProfileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2)
            return new(PanelJudgmentOutcome.Complete, null);
        var causes = results.Where(result => result.Outcome != PanelJudgeOutcome.Valid)
            .Select(result => $"{result.Judge}:{result.Reason ?? PanelV0Contract.Token(result.Outcome)}").ToList();
        if (results.Count != 2) causes.Add("missing-judgment");
        if (valid.Any(result => !ProviderBoundPanelJudgeBinding.Supported(result.WorkerProfileName)))
            causes.Add("provider-provenance-missing");
        if (valid.Length == 2 && valid.All(result => ProviderBoundPanelJudgeBinding.Supported(result.WorkerProfileName)))
            causes.Add("provider-not-distinct");
        return new(valid.Length == 0 ? PanelJudgmentOutcome.Skipped : PanelJudgmentOutcome.Partial,
            "judgment-incomplete:" + string.Join(';', causes));
    }
}
