using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A read-only adapter over authoritative attempt/holder capacity rules. Query failures
// refuse preflight rather than making an unproven idle claim or reconciling an attempt.
internal static class GoalLiveAcceptancePredicate
{
    internal static Func<GoalId, bool> ForWorkspace(OrchestratorWorkspace workspace) => goalId =>
    {
        try
        {
            var root = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts");
            var solo = new ConductorParallelAcceptanceAttemptCoordinator(root,
                workspace.IntegrationBranch, workspace.ExecutionDirectory);
            var directory = Path.Combine(root, goalId.Value);
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
                {
                    var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(path),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidDataException($"Empty acceptance attempt: {path}");
                    if (attempt.GoalId != goalId.Value)
                        throw new InvalidDataException($"Acceptance attempt goal mismatch: {path}");
                    if (solo.ReservesCapacityReadOnly(attempt)) return true;
                }
            var grouped = OwnedChildAttemptCoordinatorFactory.CreateGroupedGate(workspace, null);
            return grouped.ReadAll().Any(attempt => attempt.Members.Any(member => member.GoalId == goalId.Value) &&
                grouped.ReservesCapacityReadOnly(attempt));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"AcceptanceAttemptLive: idle state could not be established: {ex.Message}", ex);
        }
    };
}
