using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Provenance = Mcg.AgentOrchestrator.App.Orchestration.PreTesterChangedFileGuardClasses.Provenance;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Adapts shipping-source dependency evidence independently of the planner's Core-only policy.
internal static class PreTesterReverseDependencySelections
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    internal static string[] ShippingSourcePaths(IReadOnlyList<string> changedPaths) =>
        RepositoryChangeClassifier.Classify(changedPaths.Select(PreTesterChangedFileGuardClasses.Normalize))
            .Files.Where(file => file.Categories.Contains(RepositoryChangeCategory.Source) && !file.IsGeneratedArtifact &&
                file.Path.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static (IReadOnlyList<FindingEvidenceSelection> Selections, IReadOnlyList<Provenance> Provenance) Select(
        string worktreePath, IReadOnlyList<string> changedPaths, Action<string>? progress = null,
        Func<string, IReadOnlyList<string>, RepositoryRecheckTestSelection>? selector = null,
        Func<IReadOnlyList<string>, ReverseDependencyEvidenceCostRule.Decision>? costDecision = null)
    {
        var paths = ShippingSourcePaths(changedPaths);
        if (paths.Length == 0) return ([], []);
        if (paths.Any(path => !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(worktreePath, path))))
        {
            Degraded("non-source-or-missing-path");
            return ([], []);
        }
        RepositoryRecheckTestSelection result;
        try
        {
            result = (selector ?? RepositoryRecheckTestSelection.Read)(worktreePath, paths);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Degraded("reader-unavailable:" + error.GetType().Name);
            return ([], []);
        }
        if (!result.Resolved)
        {
            Degraded(result.Reason ?? "selection-not-resolved");
            return ([], []);
        }
        if (result.TestClasses.Count == 0) return ([], []);

        IReadOnlyList<AcceptanceTestClassDescriptor> resolved;
        try
        {
            resolved = AcceptanceTestClassSourceScanner.ScanProject(worktreePath, Project);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            resolved = [];
        }
        // Resolve the entire contribution before emitting success or creating any obligation.
        foreach (var name in result.TestClasses)
        {
            if (resolved.Count(item => item.FullName == name ||
                    item.FullName.EndsWith("." + name, StringComparison.Ordinal)) != 1)
            {
                (progress ?? Console.WriteLine)(
                    $"PRE_TESTER_GUARD_SELECTED class={name} kind=reverse-dependency-unresolved reason=class-match-not-unique path={paths[0]}");
                return ([], []);
            }
        }
        var classes = result.TestClasses.Distinct(StringComparer.Ordinal).ToArray();
        var decision = (costDecision ?? ReadCostDecision)(classes);
        if (!decision.Run)
        {
            (progress ?? Console.WriteLine)(
                $"PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-skipped-cost {decision.Line}");
            return ([], []);
        }
        var selections = classes
            .Select(name => new FindingEvidenceSelection(GoalAcceptanceVerifier.ProjectLabel(Project), name)).ToArray();
        return (selections, selections.SelectMany(selection => paths.Select(path =>
            new Provenance(selection.TestClass, "reverse-dependency", path))).ToArray());

        void Degraded(string reason) => (progress ?? Console.WriteLine)(
            $"PRE_TESTER_GUARD_SELECTED class=none kind=reverse-dependency-degraded reason={reason} path={paths[0]}");

        ReverseDependencyEvidenceCostRule.Decision ReadCostDecision(IReadOnlyList<string> selectedClasses)
        {
            var inputs = ReverseDependencyEvidenceCostHistory.Read(worktreePath,
                HostLedgerStateDirectory.Resolve(worktreePath), selectedClasses, changedPaths);
            var evaluated = ReverseDependencyEvidenceCostRule.Evaluate(
                inputs.SelectionSeconds, inputs.FullGateSeconds, inputs.FailureRate);
            return inputs.UnavailableReason is { } reason
                ? evaluated with { Line = $"{evaluated.Line} input_unavailable={reason.Replace('\r', ' ').Replace('\n', ' ')}" }
                : evaluated;
        }
    }

    internal static IEnumerable<FindingEvidenceSelection> RecordedSelections(IReadOnlyList<string> selections)
    {
        var label = GoalAcceptanceVerifier.ProjectLabel(Project);
        foreach (var selection in selections)
        {
            var parts = selection.Split(':', 2);
            if (parts.Length == 2 && parts[0].Equals(label, StringComparison.OrdinalIgnoreCase))
                yield return new(parts[0], parts[1]);
        }
    }
}
