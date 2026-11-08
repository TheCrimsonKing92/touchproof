using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Provenance = Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses.Provenance;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The impact planner owns coupled coverage and its bounds; this adapter never widens its checks.
internal static class PreTesterCoupledClassSelections
{
    internal static (IReadOnlyList<FindingEvidenceSelection> Selections, IReadOnlyList<Provenance> Provenance) Select(
        string worktreePath, IReadOnlyList<string> changedPaths, AcceptanceGateEngineSettings? settings,
        Action<string>? progress = null, IReadOnlyList<string>? structuralPaths = null)
    {
        var structural = PreTesterLiveTreeStructuralSelections.Select(worktreePath, changedPaths, structuralPaths, progress);
        // Match the request's project identity so resumed attempts can filter new structural coverage.
        var selected = structural.Selections.Select(item => settings is not null &&
            GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(item.TestProject, settings, out var project)
                ? item with { TestProject = GoalAcceptanceVerifier.ProjectLabel(project) } : item).ToList();
        var reasons = structural.Provenance.ToList();
        if (settings is null || settings.DeclaredDotnetTestChecks.Count == 0 || changedPaths.Count == 0)
            return (selected, reasons);

        var triggers = changedPaths.Select(PreTesterChangedFileGuardClasses.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RepositoryTestImpactPlan plan;
        try
        {
            plan = ManifestScopedImpactPlan.Plan(triggers, worktreePath, settings.DeclaredDotnetTestChecks);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Degraded("planner-unavailable:" + error.GetType().Name);
            return (selected, reasons);
        }
        if (plan.ReverseDependencyDegradation is not null || plan.RequiresBroadVerification)
            Degraded(plan.ReverseDependencyDegradation is { } degradation
                ? degradation.Kind + ":" + degradation.Reason +
                    (plan.RequiresBroadVerification ? ",RequiresBroadVerification" : "")
                : "RequiresBroadVerification");

        foreach (var check in plan.Checks)
        {
            var projectIndex = check.Command.ToList().IndexOf("--project");
            if (check.TestClassSelections is not { Count: > 0 } names) continue;
            if (projectIndex < 0 || projectIndex + 1 >= check.Command.Count)
                throw new InvalidDataException("Pre-Tester impact check names classes without a test project.");
            var project = check.Command[projectIndex + 1];
            var label = GoalAcceptanceVerifier.ProjectLabel(project);
            // Resolve within the check's project, including projects nested under another test source root.
            // Unavailable candidate inventories leave planner hints unresolved rather than creating NotRun obligations.
            IReadOnlyList<AcceptanceTestClassDescriptor> resolved;
            try
            {
                resolved = AcceptanceTestClassSourceScanner.ScanProject(worktreePath, project);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                resolved = [];
            }
            foreach (var name in names)
            {
                if (resolved.Count(item => item.FullName == name ||
                        item.FullName.EndsWith("." + name, StringComparison.Ordinal)) != 1)
                {
                    (progress ?? Console.WriteLine)(
                        $"PRE_TESTER_GUARD_SELECTED class={name} kind=coupled-impact-unresolved path={triggers[0]}");
                    continue;
                }
                selected.Add(new(label, name));
                reasons.AddRange(triggers.Select(path => new Provenance(name, "coupled-impact", path)));
            }
        }
        return (selected.Distinct().ToArray(), reasons.Distinct().ToArray());

        void Degraded(string reason) => (progress ?? Console.WriteLine)(
            $"PRE_TESTER_GUARD_SELECTED class=none kind=coupled-impact-degraded reason={reason} path={triggers[0]}");
    }

    internal static IReadOnlyList<string>? ReadStructuralPaths(string worktreePath, string mainRevision)
    {
        if (mainRevision.Length is not (40 or 64) || !mainRevision.All(Uri.IsHexDigit)) return null;
        var result = GitCli.Run(worktreePath, "diff", "--no-renames", "--name-status", "-z", mainRevision, "HEAD", "--");
        return result.Succeeded && !result.DrainTimedOut ? ParseStructuralPaths(result.Output) : null;
    }

    internal static IReadOnlyList<string> ParseStructuralPaths(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length % 2 != 0) throw new InvalidDataException("Malformed pre-Tester structural git diff.");
        var paths = new List<string>();
        for (var index = 0; index < fields.Length; index += 2)
        {
            if (fields[index] is "A" or "D")
                paths.Add(PreTesterChangedFileGuardClasses.Normalize(fields[index + 1]));
        }
        return paths;
    }
}
