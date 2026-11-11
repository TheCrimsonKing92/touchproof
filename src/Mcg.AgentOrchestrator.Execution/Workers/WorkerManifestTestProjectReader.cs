using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerManifestTestProjectReader
{
    internal static string ResolvePath(string workingDirectory, WorkerTargetHome targetHome) =>
        targetHome.AcceptanceManifestPath is { } resolveManifest
            ? resolveManifest(workingDirectory)
            : Path.Combine(workingDirectory, "config", "acceptance-manifest.json");

    internal static ManifestDeclaredTestProjects? Read(string? manifestPath) =>
        ReadChecks(manifestPath) is { } checks
            ? ManifestDeclaredTestProjects.From(checks.Select(check => check.Project))
            : null;

    internal static IReadOnlyList<(string Name, string Project)>? ReadChecks(string? manifestPath) =>
        WorkerManifestTestCheckReader.Read(manifestPath)?.Checks
            .Where(check => check.Kind == ManifestTestCheckKinds.Dotnet)
            .Select(check => (check.Name, check.Project!)).ToArray();
}
