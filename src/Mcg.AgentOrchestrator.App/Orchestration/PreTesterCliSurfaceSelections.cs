using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Project membership is discovered for a new attempt, never inferred as failure attribution.
internal static class PreTesterCliSurfaceSelections
{
    internal const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";

    internal static (IReadOnlyList<FindingEvidenceSelection> Selections,
        IReadOnlyList<PreTesterChangedFileGuardClasses.Provenance> Provenance) Select(
        string worktreePath, IReadOnlyList<string> changedPaths, AcceptanceGateEngineSettings? settings)
    {
        var triggers = RepositoryChangeClassifier.Classify(changedPaths.Select(PreTesterChangedFileGuardClasses.Normalize))
            .Files.Where(file => file.Categories.Contains(RepositoryChangeCategory.Source) && !file.IsGeneratedArtifact &&
                file.Path.StartsWith("src/Mcg.AgentOrchestrator.App/Cli/", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (triggers.Length == 0) return ([], []);
        if (!GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(Project, settings, out var project) ||
            !string.Equals(project, Project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Pre-Tester CLI surface inventory cannot resolve '{Project}'.");

        var selections = AcceptanceTestClassSourceScanner.ScanProject(worktreePath, project)
            .Select(item => new FindingEvidenceSelection(GoalAcceptanceVerifier.ProjectLabel(project), item.FullName)).ToArray();
        return (selections, selections.SelectMany(selection => triggers.Select(path =>
            new PreTesterChangedFileGuardClasses.Provenance(selection.TestClass, "cli-surface", path))).ToArray());
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
