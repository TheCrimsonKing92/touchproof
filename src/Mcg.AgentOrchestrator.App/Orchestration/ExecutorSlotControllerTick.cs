using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ExecutorSlotControllerTick
{
    internal static IReadOnlyList<string> Service(OperatorIntentCoordinator intents,
        AgentOrchestratorKernel kernel, Action<string> emitProgress)
    {
        var lines = intents.ExecuteWorkspacePending(kernel);
        try
        {
            foreach (var line in intents.EvaluateExecutorSlots()) emitProgress(line);
        }
        catch (Exception error)
        {
            emitProgress($"EXECUTOR_SLOT_CONTROLLER result=failed reason={JsonSerializer.Serialize(error.Message)}");
        }
        return lines;
    }
}
