using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectLearnedManifestSource
{
    internal static (LearnedAcceptanceManifestResult? Result, string? Error) Learn(
        OrchestratorProject project, string directory, Func<CrosswalkStackReader>? crosswalkReaderFactory)
    {
        var adapter = ProjectDiscoveryAdapterSelector.Select(project.RootDirectory,
            (crosswalkReaderFactory ?? CrosswalkExecutableComposition.ForOnboarding)());
        var model = adapter.Discover(project.RootDirectory, RepositorySourceInventory.ExcludedDirectoryNames,
            null, UnitCommandKinds.None);
        try
        {
            var stored = ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(directory, "project-model.json")));
            model = OverlayMeasurements(model, stored);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Unavailable stored data leaves discovery's empty measurement list in place.
        }

        var (answers, error) = ProjectAnswerCommand.ReadAnswersForOutput(directory);
        if (error is not null) return (null, error);
        var mtpLayoutRefusals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var check in LearnedTestCheckDeriver.Derive(model, OwnerAnswerApplier.Apply(model, answers)).Checks
            .Where(check => check.Runner == "mtp"))
            if (MtpBuildLayoutReader.ReadRefusal(project.RootDirectory, check.ProjectPath) is { } reason)
                mtpLayoutRefusals[check.ProjectPath] = reason;
        return (LearnedAcceptanceManifest.Build(model, answers, mtpLayoutRefusals), null);
    }

    internal static ProjectModel OverlayMeasurements(ProjectModel fresh, ProjectModel stored)
    {
        var unitIds = fresh.Units.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        return fresh with { Measurements = stored.Measurements.Where(measurement => unitIds.Contains(measurement.UnitId)).ToArray() };
    }
}
