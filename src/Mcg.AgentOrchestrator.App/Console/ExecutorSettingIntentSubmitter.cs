using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// The console queues requests; only the conductor applies executor settings.
internal sealed class ExecutorSettingIntentSubmitter(OrchestratorWorkspace workspace)
{
    internal sealed record Submission(string IntentId, string ExecutorId, bool Enabled);

    internal Submission SubmitToggle(string executorId)
    {
        var path = RemoteLaneExecutorConfiguration.ResolveStorePath(workspace.ExecutionDirectory);
        var executor = RemoteLaneExecutorConfiguration.LoadExecutors(path)
            .FirstOrDefault(entry => entry.Id == executorId)
            ?? throw new ArgumentException($"Executor '{executorId}' not found in the executor store; nothing queued.");
        var enabled = !executor.AdmissionEnabled;
        var id = Guid.NewGuid().ToString("n");
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(new OperatorIntentRecord(id, Guid.NewGuid().ToString("n"),
                OperatorIntentVerbs.ExecutorSetEnabled, OperatorIntentScopes.Workspace, null,
                JsonSerializer.Serialize(new ExecutorSetEnabledOperatorIntentPayload(executor.Id, enabled), OperatorIntentJson.Options),
                [], "operator", "owner-console", "local-process", DateTimeOffset.UtcNow,
                ActorKind: OperatorActorKind.Human)).GetAwaiter().GetResult();
        return new(persisted.Id, executor.Id, enabled);
    }
}
