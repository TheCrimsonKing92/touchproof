using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReverseDependencyBreadthBound
{
    internal const int MaximumSelectedClasses = 33;

    private const string CheckName = ReverseDependencyEvidenceCostGate.CheckName;
    private const string DeferredMarker = "reach-deferred-breadth:";
    private static readonly string[] HubFiles =
    [
        "src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs",
        "src/Mcg.AgentOrchestrator.Execution/Processes/PipeDrain.cs",
        "src/Mcg.AgentOrchestrator.Execution/Persistence/SqliteOrchestratorStateRepository.cs"
    ];

    internal static bool IsDeferred(RepositoryTestImpactPlan plan) =>
        plan.Summary?.Contains(DeferredMarker, StringComparison.Ordinal) == true;

    internal static RepositoryTestImpactPlan Apply(RepositoryTestImpactPlan plan, IEnumerable<string> changedPaths)
    {
        var reverseChecks = plan.Checks.Where(check => check.Name == CheckName).ToArray();
        if (reverseChecks.Length == 0) return plan;

        var selectedCount = reverseChecks.SelectMany(check => check.TestClassSelections ?? [])
            .Distinct(StringComparer.Ordinal).Count();
        var changed = changedPaths.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reasons = new List<string>();
        if (selectedCount > MaximumSelectedClasses)
            reasons.Add(FormattableString.Invariant($"selected_classes={selectedCount} exceeds bound={MaximumSelectedClasses}"));

        reasons.AddRange(HubFiles.Where(changed.Contains).Order(StringComparer.Ordinal).Select(path => $"hub_file={path}"));
        if (reasons.Count == 0) return plan;

        var deferred = $"{DeferredMarker} reverse-dependent breadth was deferred to the full gate; reason=" +
            string.Join("; ", reasons);
        return plan with
        {
            Checks = plan.Checks.Where(check => check.Name != CheckName).ToArray(),
            Summary = string.IsNullOrEmpty(plan.Summary) ? deferred : $"{plan.Summary} {deferred}"
        };
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
