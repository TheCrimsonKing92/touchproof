using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class CliSoloBaselineRepairIntake(OrchestratorWorkspace workspace) : ISoloBaselineRepairIntake
{
    public GoalCreationOperatorIntentPayload PrepareRepair(string requestKey, string objective, string epicId)
    {
        IReadOnlyList<string> arguments = ["goal", objective, "--epic", epicId, "--request-key", requestKey];
        var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
        var request = GoalIntakeRequestMapper.Map(arguments, workspace, agents)!;
        return new(requestKey, request.Fingerprint, request.Mode, objective, epicId, arguments, null, agents, false);
    }

    public void ApplyReservedRepair(AgentOrchestratorKernel kernel, GoalCreationOperatorIntentPayload payload) =>
        _ = new GoalCreationOperatorIntentHandler(workspace, new CliGoalIntentConstruction()).Apply(kernel, payload, out _);
}
