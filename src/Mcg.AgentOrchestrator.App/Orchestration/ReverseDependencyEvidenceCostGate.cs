using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReverseDependencyEvidenceCostGate
{
    internal const string CheckName = "focused reverse-dependent infrastructure tests";

    internal static RepositoryTestImpactPlan Apply(RepositoryTestImpactPlan plan,
        Func<IReadOnlyList<string>, ReverseDependencyEvidenceCostInputs> readInputs, Action<string>? log = null)
    {
        var checks = plan.Checks.Where(check => check.Name == CheckName).ToArray();
        if (checks.Length == 0) return plan;
        var selections = checks.SelectMany(check => check.TestClassSelections ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        var inputs = checks.Any(check => check.TestClassSelections is null)
            ? ReverseDependencyEvidenceCostInputs.Unavailable("selection classes unavailable") : readInputs(selections);
        var decision = ReverseDependencyEvidenceCostRule.Evaluate(
            inputs.SelectionSeconds, inputs.FullGateSeconds, inputs.FailureRate);
        var line = inputs.UnavailableReason is { } reason
            ? $"{decision.Line} input_unavailable={reason.Replace('\r', ' ').Replace('\n', ' ')}" : decision.Line;
        (log ?? Console.WriteLine)(line);
        if (decision.Run && inputs.UnavailableReason is null) return plan;
        var classes = inputs.UnavailableReason is not null || selections.Length == 0
            ? "selection classes unavailable" : string.Join(", ", selections);
        var skipped = $"reach-skipped-cost: {classes} not run before review; {line}";
        return plan with
        {
            Checks = plan.Checks.Where(check => check.Name != CheckName).ToArray(),
            Summary = string.IsNullOrEmpty(plan.Summary) ? skipped : $"{plan.Summary} {skipped}"
        };
    }
}
