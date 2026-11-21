using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IGoalIntentConstruction
{
    bool IsSupportedCommand(IReadOnlyList<string> arguments);

    void Build(
        GoalCreationOperatorIntentPayload payload,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IGoalLifecycleEventWriter eventWriter,
        CollaborationItemRaise raiseCollaborationItem,
        Action<Goal, IReadOnlyCollection<string>?> finalize);

    void ValidatePrepared(
        AgentOrchestratorKernel currentKernel,
        GoalSnapshot prepared,
        OrchestratorWorkspace workspace,
        IReadOnlyCollection<string>? sourceDependencies,
        bool allowTerminalSourceClaimTakeover);

    void Complete(
        GoalCreationOperatorIntentPayload payload,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        bool replayed);
}

internal sealed class GoalCreationOperatorIntentHandler(
    OrchestratorWorkspace workspace, IGoalIntentConstruction construction,
    IOrchestratorStateOutboxRepository? outboxRepository = null)
{
    private readonly GoalIntakeRequestStore _requests = new(workspace.SqliteStatePath);
    private readonly IOrchestratorStateOutboxRepository _repository =
        outboxRepository ?? new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);

    internal bool Apply(AgentOrchestratorKernel workingKernel, GoalCreationOperatorIntentPayload payload, out string summary)
    {
        if (!construction.IsSupportedCommand(payload.CreationArguments))
        {
            throw new InvalidOperationException("GOAL_INTAKE_PAYLOAD_INVALID reason=unexpected-key-or-command");
        }

        GoalIntakeRequestRecord receipt;
        bool replayed;
        try
        {
            // Construction needs the existing goals for dependency resolution, intake planning
            // and source claims. The scoped writer saves only the new goal and its outbox.
            var existing = _requests.Get(payload.RequestKey);
            var ids = existing?.State == GoalIntakeRequestStates.StillCommitting
                ? _repository.ListGoalMetadataAsync().GetAwaiter().GetResult().Select(goal => new GoalId(goal.Id)).ToArray()
                : [];
            (receipt, replayed) = _repository.TransactGoalCreationWithOutboxAsync(
                new GoalCreationLoadScope(ids, []),
                (kernel, _) => Task.FromResult(BuildAndBind(kernel, payload))).GetAwaiter().GetResult();
        }
        catch (ConstructionFailedException ex)
        {
            // The state transaction has rolled back all goal and source-claim changes.
            var cause = ex.InnerException!;
            try
            {
                receipt = _requests.MarkFailed(payload.RequestKey, payload.Fingerprint,
                    ResolveFailureCode(cause), cause.Message);
            }
            catch (Exception failure)
            {
                throw new GoalCreationIntentDeferredException(failure);
            }
            replayed = false;
        }
        catch (Exception ex) when (ex is not InvalidReceiptException)
        {
            // A storage failure leaves the claim recoverable. Reapplication checks the durable
            // binding before constructing anything, including after an uncertain commit.
            throw new GoalCreationIntentDeferredException(ex);
        }

        if (receipt.State == GoalIntakeRequestStates.Failed)
            throw new InvalidOperationException($"GOAL_INTAKE_REPLAY_FAILED requestKey={payload.RequestKey} " +
                $"failureCode={receipt.FailureCode} detail={receipt.FailureDetail}");

        var goalId = new GoalId(receipt.GoalId!);
        try
        {
            if (!workingKernel.Goals.Any(goal => goal.Id == goalId))
            {
                var snapshot = _repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=created-goal-missing");
                workingKernel.ReplaceGoalWithSnapshot(snapshot);
            }

            var goal = workingKernel.GetGoal(goalId);
            _ = GoalCreationSideEffectDelivery.DeliverPending(_repository, workspace, workingKernel, goalId);
            if (!replayed)
                _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goalId);

            construction.Complete(payload, workingKernel, workspace, goal, replayed);
            summary = $"goal={goalId.Value}";
        }
        catch (Exception ex)
        {
            throw new GoalCreationIntentDeferredException(GoalCreationSideEffectDelivery.Incomplete(goalId, ex));
        }
        return replayed;
    }

    private (bool ShouldSave, (GoalIntakeRequestRecord Receipt, bool Replayed) Result,
        IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages) BuildAndBind(
        AgentOrchestratorKernel kernel, GoalCreationOperatorIntentPayload payload)
    {
        var current = _requests.Get(payload.RequestKey);
        if (current is null || current.FingerprintVersion != GoalIntakeRequestStore.CurrentFingerprintVersion ||
            !string.Equals(current.Fingerprint, payload.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidReceiptException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=missing-or-mismatched-reservation");
        }
        if (current.State is GoalIntakeRequestStates.Created or GoalIntakeRequestStates.Failed)
            return (false, (current, true), []);
        if (current.State != GoalIntakeRequestStates.StillCommitting)
            throw new InvalidReceiptException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=unknown-state");

        var events = new DeferredGoalLifecycleEventWriter();
        var collaboration = new DeferredGoalCreationCollaborationWriter(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        GoalSnapshot? prepared = null;
        try
        {
            // Prepare on a detached kernel so preconditions see the pre-creation state and
            // nothing from a failed build can enter the live conductor's working set.
            var constructionKernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            try
            {
                construction.Build(payload, constructionKernel, workspace, events, collaboration.RaiseAsync,
                    (goal, sourceDependencies) =>
                    {
                        GoalRefinementWorkCoordinator.RecordPending(constructionKernel, goal.Id);
                        prepared = constructionKernel.ExportGoalSnapshot(goal.Id);
                        construction.ValidatePrepared(kernel, prepared, workspace, sourceDependencies,
                            payload.AllowTerminalSourceClaimTakeover);
                        GoalCreationSideEffectDelivery.BeforeStateCommit(goal.Id);
                        // The shared CLI handler would next deliver side effects or start work.
                        // End construction here so those steps can only run after commit.
                        throw new ConstructionReadyException();
                    });
            }
            catch (ConstructionReadyException) { }
            if (prepared is null)
                throw new InvalidOperationException("GOAL_CREATE_COMMIT_MISSING reason=finalizer-not-invoked");
        }
        catch (Exception ex)
        {
            throw new ConstructionFailedException(ex);
        }

        var goalId = new GoalId(prepared.Id);
        kernel.ReplaceGoalWithSnapshot(prepared);
        var receipt = _requests.MarkCreated(payload.RequestKey, payload.Fingerprint, prepared.Id);
        return (true, (receipt, false),
        [
            GoalCreationSideEffectDelivery.CreateMessage(goalId, collaboration.SnapshotEffects(), events.SnapshotEffects()),
            GoalRefinementWorkCoordinator.CreateMessage(goalId)
        ]);
    }

    private static string ResolveFailureCode(Exception exception)
    {
        var firstToken = exception.Message
            .Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (firstToken is not null &&
            firstToken.Length <= 100 &&
            firstToken.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_'))
        {
            return firstToken;
        }

        return exception is ArgumentException
            ? "GOAL_INTAKE_ARGUMENT_INVALID"
            : "GOAL_INTAKE_CREATE_FAILED";
    }

    private sealed class ConstructionFailedException(Exception cause) : Exception(cause.Message, cause);
    private sealed class ConstructionReadyException : Exception { }
    private sealed class InvalidReceiptException(string message) : InvalidOperationException(message);
}

internal sealed class GoalCreationIntentDeferredException(Exception cause) : Exception(cause.Message, cause);
