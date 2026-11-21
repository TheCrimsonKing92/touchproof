using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IGoalCreationOperatorIntentHandler
{
    bool Apply(AgentOrchestratorKernel kernel, GoalCreationOperatorIntentPayload payload, out string summary);
}

internal sealed class GoalCreationIntentDeferredException(Exception cause) : Exception(cause.Message, cause);
