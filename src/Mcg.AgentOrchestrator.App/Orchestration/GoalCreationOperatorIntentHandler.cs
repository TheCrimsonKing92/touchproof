using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class GoalCreationOperatorIntentHandler(
    OrchestratorWorkspace workspace, IOrchestratorStateOutboxRepository repository)
{
    private readonly GoalIntakeRequestStore _requests = new(workspace.SqliteStatePath);

    internal bool Apply(AgentOrchestratorKernel workingKernel, OperatorIntentRecord intent, out string summary)
    {
        var payload = JsonSerializer.Deserialize<GoalCreationOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
            ?? throw new InvalidOperationException("GOAL_INTAKE_PAYLOAD_INVALID reason=missing-payload");
        if (payload.RequestKey != intent.IdempotencyKey ||
            !CliKeyedGoalCreationSubmission.IsCommand(payload.CreationArguments))
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
                ? repository.ListGoalMetadataAsync().GetAwaiter().GetResult().Select(goal => new GoalId(goal.Id)).ToArray()
                : [];
            (receipt, replayed) = repository.TransactGoalCreationWithOutboxAsync(
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
                    CliKeyedGoalCreationSubmission.ResolveGoalIntakeFailureCode(cause), cause.Message);
            }
            catch (Exception failure)
            {
                throw new ApplyDeferredException(failure);
            }
            replayed = false;
        }
        catch (Exception ex) when (ex is not InvalidReceiptException)
        {
            // A storage failure leaves the claim recoverable. Reapplication checks the durable
            // binding before constructing anything, including after an uncertain commit.
            throw new ApplyDeferredException(ex);
        }

        if (receipt.State == GoalIntakeRequestStates.Failed)
            throw new InvalidOperationException($"GOAL_INTAKE_REPLAY_FAILED requestKey={payload.RequestKey} " +
                $"failureCode={receipt.FailureCode} detail={receipt.FailureDetail}");

        var goalId = new GoalId(receipt.GoalId!);
        try
        {
            if (!workingKernel.Goals.Any(goal => goal.Id == goalId))
            {
                var snapshot = repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=created-goal-missing");
                workingKernel.ReplaceGoalWithSnapshot(snapshot);
            }

            var goal = workingKernel.GetGoal(goalId);
            _ = CliPersistentStateRunner.DeliverGoalCreationSideEffects(repository, workspace, workingKernel, goalId);
            if (!replayed)
                _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goalId);
            if (payload.RequestedEpicId is not null || goal.SourceBacklogItemId is not null)
            {
                var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
                if (portfolio.GetGoalMembershipAsync(goalId.Value).GetAwaiter().GetResult() is null)
                {
                    var epicId = payload.RequestedEpicId ?? EpicAtCreation.ResolveGoalEpic(workspace, null, goal.SourceBacklogItemId);
                    EpicAtCreation.AssignAfterCommit(workspace, PortfolioMemberKind.Goal, goalId.Value, epicId);
                }
            }

            summary = $"goal={goalId.Value}";
            if (!replayed)
            {
                if (payload.Mode == "full" && !CliCommandHandlers.HasCliConfirmation(payload.CreationArguments, "--run"))
                    GoalCitedDefectAdvisory.Warn(goal.Objective, () => workingKernel.Goals, goal, workspace.ConductEventsLogPath);
                if (GoalCreationSummaryView.IsRequested(payload.CreationArguments))
                    GoalCreationSummaryView.Print(goal, "created", workspace.PortfolioStorePath);
                try { RunRequestedFollowUp(payload, workingKernel, goal); }
                catch (Exception ex)
                {
                    summary += $"; follow-up failed: {ex.Message.ReplaceLineEndings(" ")}";
                    Console.Error.WriteLine(ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            throw new ApplyDeferredException(GoalCreationSideEffectDelivery.Incomplete(goalId, ex));
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
            var args = payload.CreationArguments.ToArray();
            var briefPath = payload.BriefFileValueIndex is null ? null :
                Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("goal-creation"), $"{Guid.NewGuid():N}.txt");
            try
            {
                if (payload.BriefFileValueIndex is { } index)
                {
                    File.WriteAllText(briefPath!, payload.Objective);
                    args[index] = briefPath!;
                }

                var agents = payload.Agents;
                var profiles = WorkerProfileCatalog.Default();
                Goal? currentGoal = null;
                _ = CliCommandDispatcher.ExecuteCommand(args, constructionKernel, workspace, ref agents,
                    new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal, eventWriter: events,
                    refinementCollaborationItemRaise: collaboration.RaiseAsync, finalizeGoalCreation: (goal, sourceDependencies) =>
                    {
                        GoalRefinementWorkCoordinator.RecordPending(constructionKernel, goal.Id);
                        prepared = constructionKernel.ExportGoalSnapshot(goal.Id);
                        GoalCreationPreconditionValidator.Validate(kernel, prepared, workspace,
                            preparedSourceBacklogDependencies: sourceDependencies,
                            allowTerminalSourceClaimTakeover: payload.AllowTerminalSourceClaimTakeover);
                        GoalCreationSideEffectDelivery.BeforeStateCommit(goal.Id);
                        // The shared CLI handler would next deliver side effects or start work.
                        // End construction here so those steps can only run after commit.
                        throw new ConstructionReadyException();
                    });
            }
            catch (ConstructionReadyException) { }
            finally
            {
                if (briefPath is not null) File.Delete(briefPath);
            }
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

    private void RunRequestedFollowUp(GoalCreationOperatorIntentPayload payload, AgentOrchestratorKernel workingKernel, Goal goal)
    {
        var args = payload.CreationArguments;
        var run = payload.Mode == "full" && CliCommandHandlers.HasCliConfirmation(args, "--run");
        var dispatch = payload.Mode == "simple" && CliCommandHandlers.HasCliConfirmation(args, "--dispatch");
        if (!run && !dispatch) return;

        List<string> followUp = run
            ? new List<string> { "run-goal", goal.Id.Value, "--confirm-batch-start" }
            : ["subscription-dispatch", "--goal", goal.Id.Value, "1", "--confirm-dispatch-start"];
        if (run)
        {
            foreach (var flag in new[] { "--confirm-readiness-risk", SubscriptionPromptCostGuard.CliConfirmationFlag })
                if (CliCommandHandlers.HasCliConfirmation(args, flag)) followUp.Add(flag);
            var autonomy = CliCommandHandlers.GetFlagValue(args, "--autonomy") ??
                CliCommandHandlers.GetFlagValue(args, "--autonomy-policy");
            if (autonomy is not null) followUp.AddRange(["--autonomy", autonomy]);
        }
        var agents = payload.Agents;
        var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
        Goal? currentGoal = goal;
        try
        {
            _ = CliPersistentStateRunner.ExecuteCommand(followUp, repository, workspace, ref agents,
                ProviderRegistryFactory.CreateDefaultProviders(), ref profiles, ref currentGoal);
        }
        finally
        {
            var snapshot = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult();
            if (snapshot is not null) workingKernel.ReplaceGoalWithSnapshot(snapshot);
        }
    }

    internal sealed class ApplyDeferredException(Exception cause) : Exception(cause.Message, cause);
    private sealed class ConstructionFailedException(Exception cause) : Exception(cause.Message, cause);
    private sealed class ConstructionReadyException : Exception { }
    private sealed class InvalidReceiptException(string message) : InvalidOperationException(message);
}
