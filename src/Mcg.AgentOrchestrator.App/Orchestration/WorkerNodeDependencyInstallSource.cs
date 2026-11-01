using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkerNodeDependencyInstallSource
{
    internal static Func<string, NodeDependencyInstall?> For(string? projectHomeDirectory) => worktree =>
    {
        try
        {
            var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(
                worktree, changedFiles: null, projectHomeDirectory: projectHomeDirectory);
            return (manifest.Checks ?? [])
                .Select(check => NodeDependencyInstall.FromDeclaredCheck(check.Type, check.Command, check.Arguments))
                .FirstOrDefault(install => install is not null);
        }
        catch (Exception ex) when (ex is MissingAcceptanceManifestException or IOException or
            UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    };
}
