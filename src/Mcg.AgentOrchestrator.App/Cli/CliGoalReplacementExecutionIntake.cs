using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliGoalReplacementExecution
{
    internal static bool ExecuteGoalReplacementOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
            throw new InvalidOperationException("goal-replace requires a state repository with durable outbox support.");

        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = CliPersistentStateRunner.ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        if (TryReplayCommittedGoalReplacement(
                args,
                kernel,
                claimStore,
                ref currentGoal,
                out var replayResult))
        {
            return replayResult;
        }
        var deferredEventWriter = new DeferredGoalLifecycleEventWriter();
        var deferredCollaborationWriter = new DeferredGoalCreationCollaborationWriter(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        string? committedGoalId = null;

        void FinalizeGoalReplacement(Goal preparedGoal, GoalReplacementCommand command) =>
            committedGoalId = CommitGoalReplacement(
                stateRepository, outboxRepository, workspace, kernel, claimStore,
                deferredEventWriter, deferredCollaborationWriter, preparedGoal, command);


        bool shouldSave;
        try
        {
            shouldSave = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                eventWriter: deferredEventWriter,
                refinementCollaborationItemRaise: deferredCollaborationWriter.RaiseAsync,
                finalizeGoalReplacement: FinalizeGoalReplacement);
        }
        catch (Exception ex)
        {
            if (ex is not GoalReplacementRetryableTimeoutException &&
                (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var failedRequestId) ||
                 claimStore.FindAudit(failedRequestId) is null))
            {
                TryRecordGoalReplacementPreflightFailure(claimStore, args, kernel, workspace, ex);
            }
            throw;
        }

        if (committedGoalId is null)
            throw new InvalidOperationException("GOAL_REPLACE_COMMIT_MISSING reason=finalizer-not-invoked");

        var committedKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = committedKernel.Goals.Single(goal => goal.Id.Value == committedGoalId);
        GoalTextView.PrintGoal(currentGoal);
        return shouldSave;
    }

    internal static Action<Goal, GoalReplacementCommand> CreateBacklogIntakeCorrectionFinalizer(
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        AgentOrchestratorKernel kernel,
        IDictionary<string, GoalSnapshot> checkpointBaselines)
    {
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
            throw new InvalidOperationException("backlog-intake correction requires durable outbox support.");

        return (goal, command) =>
        {
            var eventWriter = new DeferredGoalLifecycleEventWriter();
            var collaborationWriter = new DeferredGoalCreationCollaborationWriter(
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
            var successorId = CommitGoalReplacement(
                stateRepository, outboxRepository, workspace, kernel,
                new SourceBacklogClaimStore(workspace.SqliteStatePath),
                eventWriter, collaborationWriter, goal, command);
            // A later batch checkpoint must retain the policy decisions written by the transfer.
            var committed = stateRepository.LoadAsync().GetAwaiter().GetResult();
            foreach (var id in new[] { command.PredecessorGoalId, new GoalId(successorId) })
            {
                var snapshot = committed.ExportGoalSnapshot(id);
                kernel.ReplaceGoalWithSnapshot(snapshot);
                checkpointBaselines[id.Value] = snapshot;
            }
        };
    }
}
