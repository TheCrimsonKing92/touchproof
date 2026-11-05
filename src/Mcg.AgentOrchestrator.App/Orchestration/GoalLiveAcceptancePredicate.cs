using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only adapter over authoritative attempt/holder capacity rules. Query failures
// refuse preflight rather than making an unproven idle claim or reconciling an attempt.
internal static class GoalLiveAcceptancePredicate
{
    internal static Func<GoalId, bool> ForWorkspace(OrchestratorWorkspace workspace, bool requireWorkspaceSettlement = false) => goalId =>
    {
        try
        {
            var roots = requireWorkspaceSettlement
                ? new[] { "acceptance-gate-attempts", "pre-review-evidence-attempts" }
                : new[] { "acceptance-gate-attempts" };
            foreach (var rootName in roots)
            {
                var root = Path.Combine(workspace.OrchestratorDirectory, rootName);
                var solo = new ConductorParallelAcceptanceAttemptCoordinator(root,
                    workspace.IntegrationBranch, workspace.ExecutionDirectory);
                var directory = Path.Combine(root, goalId.Value);
                if (requireWorkspaceSettlement)
                {
                    RequireObservableDirectory(root);
                    RequireObservableDirectory(directory);
                }
                if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
                {
                    var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException($"Empty acceptance attempt: {path}");
                    if (attempt.GoalId != goalId.Value)
                        throw new InvalidDataException($"Acceptance attempt goal mismatch: {path}");
                    if (requireWorkspaceSettlement ? solo.OwnsWorkspaceReadOnly(attempt) : solo.ReservesCapacityReadOnly(attempt)) return true;
                }
            }
            if (requireWorkspaceSettlement)
                RequireObservableDirectory(Path.Combine(workspace.OrchestratorDirectory, "grouped-gate-attempts"));
            var grouped = OwnedChildAttemptCoordinatorFactory.CreateGroupedGate(workspace, null);
            return grouped.ReadAll().Any(attempt => attempt.Members.Any(member => member.GoalId == goalId.Value) &&
                (requireWorkspaceSettlement ? grouped.OwnsWorkspaceReadOnly(attempt) : grouped.ReservesCapacityReadOnly(attempt)));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"AcceptanceAttemptLive: idle state could not be established: {ex.Message}", ex);
        }
    };

    private static void RequireObservableDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Ownership directory is not an ordinary directory: {path}");
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }
}
