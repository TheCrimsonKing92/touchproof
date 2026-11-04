using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Preparing a new prompt may create a command; restoring one must retain the exact
// unstarted reservation and update only its prompt inputs and context receipt.
internal static class WorkerDispatchPromptPreparation
{
    internal static TaskDispatchRecord? RetainForRestoration(
        TaskSpec task, WorkerProfile profile, string workingDirectory,
        DateTimeOffset dispatchedAt, bool restorePreparedContext)
    {
        var retainedDispatch = restorePreparedContext
            ? task.LastDispatch ?? throw new InvalidOperationException("Context restoration requires a recorded dispatch.")
            : null;
        if (retainedDispatch is not null &&
            (task.LastProcess is not null || retainedDispatch.DispatchedAt != dispatchedAt ||
             retainedDispatch.WorkingDirectory != workingDirectory || retainedDispatch.WorkerName != profile.Name ||
             string.IsNullOrWhiteSpace(retainedDispatch.PromptPath)))
            throw new InvalidOperationException("Context restoration requires the exact unstarted prepared dispatch.");
        return retainedDispatch;
    }

    internal static WorkerDispatchPreparation Prepare(
        TaskBrief brief, WorkerProfile profile, ProviderKind workerProviderKind,
        string promptRoot, IReadOnlyDictionary<string, string?> dispatchVariables,
        DateTimeOffset dispatchedAt, TaskDispatchRecord? retainedDispatch)
    {
        var commandTemplate = WorkerProfileDispatcher.BuildDispatchCommandTemplate(profile, workerProviderKind, dispatchVariables);
        var preparation = retainedDispatch is not null
            ? new WorkerDispatchPreparation(retainedDispatch.PromptPath!, retainedDispatch.Command, brief.Content.Length)
            : WorkerCommandTemplate.Prepare(
                brief, profile.Name, commandTemplate, promptRoot, dispatchVariables, dispatchedAt);
        if (retainedDispatch is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(preparation.PromptPath)!);
            File.WriteAllText(preparation.PromptPath, brief.Content);
        }
        return preparation;
    }

    internal static WorkerProfileDispatchResult RecordRestoredReceipt(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        TaskDispatchRecord retainedDispatch, WorkerContextPackageReceipt? contextPackageReceipt,
        WorkerDispatchPreparation preparation)
    {
        if (contextPackageReceipt is null)
            throw new InvalidOperationException("Prepared context restoration requires a typed context package.");
        kernel.RecordDispatchContextPackageReceipt(goal.Id, task.Id, retainedDispatch.DispatchedAt, contextPackageReceipt);
        return new WorkerProfileDispatchResult(task, preparation.PromptPath);
    }
}
