namespace Mcg.AgentOrchestrator.Core;

public sealed record ConductorRelaunchDecision(bool Required, string Classification);

public static partial class RepositoryChangeClassifier
{
    // Keep these roots in sync with the ProjectReference closure of App.csproj.
    // RepositoryChangeClassifierConductorRuntimePathsTests checks that closure.
    internal static readonly string[] ConductorSourceRoots =
    [
        "src/Mcg.AgentOrchestrator.App/",
        "src/Mcg.AgentOrchestrator.Core/",
        "src/Mcg.AgentOrchestrator.Execution/",
        "src/Mcg.AgentOrchestrator.Infrastructure/",
        "src/Mcg.AgentOrchestrator.Infrastructure.Providers/"
    ];

    private static readonly string[] ConductorLauncherPaths =
    [
        "scripts/resolve-run-dir.ps1",
        "scripts/Update-AppDllGitHeadMarker.ps1",
        "mcg-orchestrator.cmd",
        "touchproof.cmd"
    ];

    public static ConductorRelaunchDecision DecideConductorRelaunch(IEnumerable<string> paths)
    {
        var classifications = Classify(paths).Files
            .Select(ClassifyConductorRelaunchPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var required in new[] { "build-system", "launcher-entry", "conductor-source" })
        {
            if (classifications.Contains(required, StringComparer.Ordinal))
            {
                return new ConductorRelaunchDecision(true, required);
            }
        }

        return new ConductorRelaunchDecision(
            false,
            classifications.Length == 0
                ? "no-changed-files"
                : string.Join('+', classifications.Order(StringComparer.Ordinal)));
    }

    private static bool IsConductorRelaunchPath(RepositoryChangedFile file) =>
        ClassifyConductorRelaunchPath(file) is "build-system" or "launcher-entry" or "conductor-source";

    private static string ClassifyConductorRelaunchPath(RepositoryChangedFile file)
    {
        if (file.IsGeneratedArtifact)
        {
            return "generated";
        }

        if (file.Categories.Contains(RepositoryChangeCategory.Documentation))
        {
            return "documentation";
        }

        if (file.Categories.Contains(RepositoryChangeCategory.BuildSystem))
        {
            return "build-system";
        }

        if (ConductorLauncherPaths.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
        {
            return "launcher-entry";
        }

        // Removal diffs can still contain the retired project's paths; they require a relaunch
        // even though the project is no longer in the active App reference closure above.
        return ConductorSourceRoots.Any(root =>
            file.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
            file.Path.StartsWith("src/Mcg.AgentOrchestrator.Infrastructure.OperatorComms/", StringComparison.OrdinalIgnoreCase)
            ? "conductor-source"
            : "outside-conductor-sources";
    }
}
