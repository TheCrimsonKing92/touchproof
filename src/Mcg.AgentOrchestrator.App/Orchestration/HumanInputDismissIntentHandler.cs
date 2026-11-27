using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class HumanInputDismissIntentHandler
{
    internal static void Apply(AgentOrchestratorKernel kernel, Goal goal, HumanInputDismissOperatorIntentPayload payload)
    {
        var request = kernel.HumanInputRequests.SingleOrDefault(candidate => candidate.Id.Value == payload.RequestId)
            ?? throw new InvalidOperationException($"Human-input request '{payload.RequestId}' was not found.");
        if (request.GoalId != goal.Id)
            throw new InvalidOperationException($"Human-input request '{request.Id}' does not belong to goal '{goal.Id}'.");

        ValidateOpenDismissible(request);
        kernel.DismissHumanInput(request.Id);
    }

    internal static void ValidateOpenDismissible(HumanInputRequest request)
    {
        if (request.IsCompleted)
            throw new InvalidOperationException($"Human-input request '{request.Id}' is already completed.");
        if (!request.IsDismissible)
            throw new InvalidOperationException($"Human-input request '{request.Id}' is not dismissible.");
    }
}
