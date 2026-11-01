using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredGateCheckSource
{
    internal static Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>> For(string? projectHomeDirectory) =>
        (_, task) =>
        {
            if (task.LastDispatch is not { } dispatch) return [];
            try
            {
                var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(
                    dispatch.WorkingDirectory, changedFiles: null, projectHomeDirectory: projectHomeDirectory);
                return (manifest.Checks ?? []).Select(check => new DeclaredGateCheck(check.Name,
                    selector => GateCheckSelectorMatcher.Matches(selector, check))).ToArray();
            }
            catch (Exception ex) when (ex is MissingAcceptanceManifestException or IOException or
                UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        };
}
