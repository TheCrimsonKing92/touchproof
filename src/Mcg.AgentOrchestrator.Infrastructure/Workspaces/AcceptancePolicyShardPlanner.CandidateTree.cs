using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class AcceptancePolicyShardPlanner
{
    internal static PolicyShardPlan BuildPolicyShardPlan(
        IReadOnlyList<string>? changedFiles,
        AcceptanceShardPolicySwitches? switches,
        ICandidateTreeProbe candidateTree,
        IEnumerable<string>? testProjects = null) =>
        BuildPolicyShardPlan(changedFiles, switches, candidateTree, AcceptanceProjectTopologySource.Home, testProjects);

    internal static PolicyShardPlan BuildPolicyShardPlan(
        IReadOnlyList<string>? changedFiles,
        AcceptanceShardPolicySwitches? switches,
        ICandidateTreeProbe candidateTree,
        AcceptanceProjectTopologySource topology,
        IEnumerable<string>? testProjects = null)
    {
        ArgumentNullException.ThrowIfNull(candidateTree);
        ArgumentNullException.ThrowIfNull(topology);
        if (!topology.ManifestAvailable)
            return PolicyShardPlan.NotApplicable("manifest missing or unreadable; no project checks derived") with { Topology = topology };
        var removedPaths = (changedFiles ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path)!)
            .Where(path => !candidateTree.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal).ToArray();
        var presentPaths = changedFiles is null ? null : changedFiles
            .Where(path => !removedPaths.Contains(NormalizePath(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var plan = BuildPresentPolicyShardPlan(presentPaths, switches, topology);
        if (!plan.ForceFull && removedPaths.Length > 0 &&
            FullShardReason(removedPaths, RepositoryChangeClassifier.Classify([]), [], switches,
                pathClausesOnly: true) is { } removedReason)
        {
            plan = PolicyShardPlan.Full(
                $"{removedReason}; {(plan.Applies ? plan.Evidence : BuildPolicyShardEvidence([], plan.DependencyClosure, topology))}",
                plan.DependencyClosure);
        }
        // Include manifest projects even outside the dependency closure: force-full and
        // focused manifest checks must obey the same absence rule as scoped selections.
        var projects = topology.Graph.Keys.Concat(
            topology.Graph.Values.SelectMany(entry => entry.ReferencingProjects))
            .Concat(topology.IsHome ? testProjects ?? [] : topology.DeclaredProjects)
            .Select(project => NormalizePath(project)!)
            .Where(project => project.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) &&
                project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var absentProjects = projects.Where(project => !candidateTree.Exists(project))
            .Order(StringComparer.Ordinal).ToArray();
        var clauses = removedPaths.Select(path => $"skipped removed path: {path} (absent from candidate tree)")
            .Concat(absentProjects.Select(project => $"skipped absent test project: {project} (absent from candidate tree)"))
            .ToArray();
        return plan with
        {
            Topology = topology,
            Evidence = clauses.Length == 0 ? plan.Evidence : $"{plan.Evidence}; {string.Join("; ", clauses)}",
            DependencyClosure = plan.DependencyClosure.Except(absentProjects, StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            AbsentProjects = absentProjects.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
