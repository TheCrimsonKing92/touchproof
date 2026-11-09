using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Failure diagnostics never provide facts or authority to reclaim a root.
internal static class GoalStoreReadFailureLabeler
{
    public static UnavailableGoalStore Describe(
        string path, string repoRoot, OrchestratorProjectRegistry? registry)
    {
        var project = ProjectName(path, repoRoot, registry);
        project = new string(project.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_').ToArray());
        string failure;
        try
        {
            failure = !File.Exists(path) ? "missing-store"
                : !StateDbMigrations.IsUpToDate(path) ? "schema-behind" : "read-failed";
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = "unreadable-store";
        }
        return new(project, failure, path);
    }

    private static string ProjectName(string path, string repoRoot, OrchestratorProjectRegistry? registry)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullPath, Path.GetFullPath(OrchestratorWorkspace.ForDirectory(repoRoot).SqliteStatePath), comparison))
            return OrchestratorWorkspace.DefaultProjectName;
        try
        {
            foreach (var project in (registry ?? OrchestratorProjectRegistry.CreateDefault()).ListProjects())
                if (Within(project.ResolveWorkspace().OrchestratorDirectory) ||
                    Within(OrchestratorWorkspace.LegacyProjectDirectory(project.RootDirectory, project.Name)))
                    return project.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Discovery already succeeded; a later registry failure must not suppress retention.
        }
        foreach (var folder in new[] { "tenants", "projects" })
        {
            var parent = Path.Combine(repoRoot, ".orchestrator", folder);
            if (Within(parent))
                return Path.GetRelativePath(parent, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        }
        return Path.GetFileName(Path.GetDirectoryName(fullPath))!;

        bool Within(string directory) => fullPath.StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, comparison);
    }
}
