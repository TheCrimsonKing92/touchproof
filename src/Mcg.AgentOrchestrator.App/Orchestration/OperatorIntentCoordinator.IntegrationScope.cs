using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class OperatorIntentCoordinator
{
    internal Func<AgentOrchestratorKernel, ConductorConflictIntegration>? IntegrationScopeOwner { get; init; }

    private void ApplyIntegrationScope(AgentOrchestratorKernel kernel, Goal goal, OperatorIntentRecord intent) =>
        (IntegrationScopeOwner ?? throw new InvalidOperationException("Integration scope owner is not configured."))(kernel)
            .ApplyScope(goal, intent);

    private void RequireIntegrationScopeAfterAnswer(AgentOrchestratorKernel kernel, Goal goal,
        AnswerOperatorIntentPayload payload)
    {
        var taskId = payload.RouteRole is not null
            ? goal.Tasks.LastOrDefault(item => item.RequiredRole == AgentRole.Developer)?.Id.Value
            : payload.TargetKind == OperatorAnswerTargetKind.HumanInput
                ? kernel.HumanInputRequests.FirstOrDefault(item => item.Id.Value == payload.TargetId && item.GoalId == goal.Id)?.TaskId?.Value
                : null;
        IntegrationScopeOwner?.Invoke(kernel).RequireScopeAfterAnswer(goal, taskId);
    }
}
