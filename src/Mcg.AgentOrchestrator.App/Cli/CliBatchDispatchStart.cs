using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Owns the batch CLI's checkpoint lifetime and result handling; persistence stays in
// CliAcceptanceCheckpoint so admission snapshots and process records share one baseline.
internal static class CliBatchDispatchStart
{
    internal static bool Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        Func<GoalId, AgentOrchestratorKernel> loadGoal)
    {
        var goalPrefix = args.Count > 1 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase)
            ? (args.Count > 2 ? args[2] : null)
            : (args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null);
        var goalId = CliPersistentStateRunner.ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, goalPrefix);
        var kernel = loadGoal(goalId);
        var checkpoints = new CliAcceptanceCheckpoint(
            stateRepository, workspace, kernel, goalId, kernel.ExportGoalSnapshot(goalId));
        currentGoal = kernel.GetGoal(goalId);
        var criticalCheckpointRejected = false;
        string? postCommitFailure = null;
        var postCommitActions = new PostCommitActions();

        void PersistCriticalCurrentGoal(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> ids) =>
            PersistCriticalGoal(checkpoints, checkpoint, ids, () => criticalCheckpointRejected = true);

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            checkpoints.Reload,
            persistCriticalGoalKernel: PersistCriticalCurrentGoal,
            recordDurableGoalBaseline: checkpoints.RecordDurableBaseline,
            registerPostCommitFailure: failure => postCommitFailure ??= failure,
            registerPostCommitAction: postCommitActions.Register);

        if ((shouldSave || postCommitFailure is not null) && !criticalCheckpointRejected)
            checkpoints.Persist(kernel, goalId);

        currentGoal = kernel.GetGoal(goalId);
        postCommitActions.Run();
        if (postCommitFailure is not null)
            throw new InvalidOperationException(postCommitFailure);

        return shouldSave;
    }

    internal static void PersistCriticalGoal(
        CliAcceptanceCheckpoint checkpoints,
        AgentOrchestratorKernel checkpoint,
        IReadOnlyCollection<GoalId> changedGoalIds,
        Action rejected)
    {
        try
        {
            checkpoints.PersistCritical(checkpoint, changedGoalIds);
        }
        catch (DispatchCheckpointConflictException)
        {
            // Rejection already rebased to authoritative state. A later write must not
            // reopen a second-writer window over a newer operator update.
            rejected();
            throw;
        }
    }

    internal static bool StartPrepared(CliExecutionContext context)
    {
        var started = new Mcg.AgentOrchestrator.App.Application.GoalDispatchOperations().StartDispatches(
            context.Kernel,
            context.Workspace,
            context.CurrentGoal!,
            context.Agents,
            context.WorkerProfiles,
            context.Providers,
            refreshBeforeStart: false,
            checkpointBeforeWorkerStart: (kernel, goalId, taskId, phase) =>
                ConductorBatchLoop.PersistCriticalDispatchStartOrThrow(
                    context.PersistCriticalGoalCheckpoint, kernel, goalId, taskId, phase),
            recordDurableGoalBaseline: context.RecordDurableGoalBaseline);
        DispatchResultTextView.PrintProcessBatchResult(context.CurrentGoal!, started);
        if (started.StartFailures?.FirstOrDefault() is { } batchStartFailure)
        {
            context.FailAfterCommit(batchStartFailure.Reason);
        }

        return started.Tasks.Count > 0 || started.StartFailures?.Count > 0;
    }
}
