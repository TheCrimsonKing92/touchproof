using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
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
    private static readonly Regex OpeningBacklogReference = new(
        @"\A\s*\(?\s*backlog\s+([0-9a-f]{8,64})\s*\)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        GoalSnapshot? committedSnapshot = null;
        try
        {
            receipt = ReadReservation(payload);
            replayed = receipt.State is GoalIntakeRequestStates.Created or GoalIntakeRequestStates.Failed;
            if (!replayed)
            {
                var prepared = Prepare(workingKernel, payload);
                var sourceItem = prepared.Snapshot.SourceBacklogItemId is { } sourceId
                    ? new BacklogStore(workspace.BacklogStorePath).GetByExactIdAsync(sourceId).GetAwaiter().GetResult()
                    : null;
                var scope = GoalCreationLoadScope.ForPreparedGoal(prepared.Snapshot, sourceItem);
                var preparedGoalId = new GoalId(prepared.Snapshot.Id);

                (receipt, replayed) = _repository.TransactGoalCreationWithOutboxAsync<(
                    GoalIntakeRequestRecord Receipt, bool Replayed)>(scope, (kernel, _) =>
                {
                    var current = ReadReservation(payload);
                    if (current.State is GoalIntakeRequestStates.Created or GoalIntakeRequestStates.Failed)
                        return Task.FromResult((false, (current, true), (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));

                    ValidatePrepared(kernel, prepared, payload, preparedGoalId);
                    kernel.ReplaceGoalWithSnapshot(prepared.Snapshot);
                    var bound = _requests.MarkCreated(payload.RequestKey, payload.Fingerprint, prepared.Snapshot.Id);
                    IReadOnlyList<OrchestratorStateOutboxMessage> outbox =
                    [
                        GoalCreationSideEffectDelivery.CreateMessage(preparedGoalId,
                            prepared.Collaboration.SnapshotEffects(), prepared.Events.SnapshotEffects()),
                        GoalRefinementWorkCoordinator.CreateMessage(preparedGoalId)
                    ];
                    return Task.FromResult((true, (bound, false), outbox));
                }).GetAwaiter().GetResult();
                if (!replayed)
                    committedSnapshot = prepared.Snapshot;
            }
        }
        catch (ConstructionFailedException ex)
        {
            // Preparation is detached, and a failure during validation rolls back the state transaction.
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
            replayed = receipt.State == GoalIntakeRequestStates.Created;
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
                var snapshot = committedSnapshot ?? _repository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
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

    private GoalIntakeRequestRecord ReadReservation(GoalCreationOperatorIntentPayload payload)
    {
        var current = _requests.Get(payload.RequestKey);
        if (current is null || current.FingerprintVersion != GoalIntakeRequestStore.CurrentFingerprintVersion ||
            !string.Equals(current.Fingerprint, payload.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidReceiptException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=missing-or-mismatched-reservation");
        }
        if (current.State is not (GoalIntakeRequestStates.StillCommitting or GoalIntakeRequestStates.Created or GoalIntakeRequestStates.Failed))
            throw new InvalidReceiptException($"GOAL_INTAKE_RECEIPT_INVALID requestKey={payload.RequestKey} reason=unknown-state");

        return current;
    }

    private PreparedCreation Prepare(AgentOrchestratorKernel workingKernel, GoalCreationOperatorIntentPayload payload)
    {
        var constructionKernel = AgentOrchestratorKernel.FromSnapshot(workingKernel.ExportSnapshot());
        HydrateConstructionInputs(constructionKernel, payload);
        var events = new DeferredGoalLifecycleEventWriter();
        var collaboration = new DeferredGoalCreationCollaborationWriter(CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        GoalSnapshot? prepared = null;
        IReadOnlyCollection<string>? sourceDependencies = null;
        try
        {
            try
            {
                construction.Build(payload, constructionKernel, workspace, events, collaboration.RaiseAsync,
                    (goal, dependencies) =>
                    {
                        GoalRefinementWorkCoordinator.RecordPending(constructionKernel, goal.Id);
                        prepared = constructionKernel.ExportGoalSnapshot(goal.Id);
                        sourceDependencies = dependencies;
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

        return new(prepared, sourceDependencies, collaboration, events);
    }

    private void HydrateConstructionInputs(AgentOrchestratorKernel kernel, GoalCreationOperatorIntentPayload payload)
    {
        var arguments = payload.CreationArguments;
        if (CliCommandHandlers.HasCliConfirmation(arguments, "--run") ||
            CliCommandHandlers.HasCliConfirmation(arguments, "--dispatch"))
            return;

        var goalIds = FindExplicitDependencyIds(arguments);
        var backlogPrefix = CliCommandHandlers.GetFlagValue(arguments, "--backlog-item");
        if (string.IsNullOrWhiteSpace(backlogPrefix))
            backlogPrefix = OpeningBacklogReference.Match(payload.Objective).Groups[1].Value;

        BacklogItem? sourceItem;
        try
        {
            sourceItem = BacklogItemIdPrefixResolver.Resolve(workspace.BacklogStorePath, backlogPrefix, explicitFlag: false);
        }
        catch (InvalidOperationException ex)
        {
            throw new ConstructionFailedException(ex);
        }

        if (sourceItem is not null)
        {
            var backlogIds = new List<string> { sourceItem.Id };
            foreach (var dependency in sourceItem.Dependencies)
            {
                if (dependency.TargetKind == BacklogDependencyTargetKind.Goal)
                    goalIds.Add(new GoalId(dependency.PrerequisiteId));
                else
                    backlogIds.Add(dependency.PrerequisiteId);
            }
            goalIds.UnionWith(_repository.ListBacklogRelatedGoalIdsAsync(backlogIds).GetAwaiter().GetResult());
        }

        var loadedIds = kernel.Goals.Select(goal => goal.Id).ToHashSet();
        foreach (var id in goalIds.Except(loadedIds))
        {
            if (_repository.LoadGoalAsync(id).GetAwaiter().GetResult() is { } snapshot)
                kernel.ReplaceGoalWithSnapshot(snapshot);
        }
    }

    private HashSet<GoalId> FindExplicitDependencyIds(IReadOnlyList<string> arguments)
    {
        if (!arguments[0].Equals("goal", StringComparison.OrdinalIgnoreCase))
            return [];

        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < arguments.Count - 1; index++)
        {
            if (!arguments[index].Equals("--depends-on", StringComparison.OrdinalIgnoreCase))
                continue;

            var prefix = arguments[++index];
            if (prefix.Length > 0 && prefix.All(char.IsAsciiHexDigit))
                prefixes.Add(prefix);
        }
        if (prefixes.Count == 0)
            return [];

        // Stored goal ids are lower-case GUIDs. Read at most two ids per prefix so the CLI
        // resolver still detects ambiguity when a match is outside the live kernel.
        using var connection = StateDbConnectionFactory.Open(workspace.SqliteStatePath, StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM goals WHERE id GLOB $prefix LIMIT 2";
        var parameter = command.Parameters.AddWithValue("$prefix", "");
        var ids = new HashSet<GoalId>();
        foreach (var prefix in prefixes)
        {
            parameter.Value = prefix.ToLowerInvariant() + "*";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                ids.Add(new GoalId(reader.GetString(0)));
        }

        return ids;
    }

    private void ValidatePrepared(AgentOrchestratorKernel kernel, PreparedCreation prepared,
        GoalCreationOperatorIntentPayload payload, GoalId goalId)
    {
        try
        {
            construction.ValidatePrepared(kernel, prepared.Snapshot, workspace, prepared.SourceDependencies,
                payload.AllowTerminalSourceClaimTakeover);
            GoalCreationSideEffectDelivery.BeforeStateCommit(goalId);
        }
        catch (Exception ex)
        {
            throw new ConstructionFailedException(ex);
        }
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

    private sealed record PreparedCreation(
        GoalSnapshot Snapshot,
        IReadOnlyCollection<string>? SourceDependencies,
        DeferredGoalCreationCollaborationWriter Collaboration,
        DeferredGoalLifecycleEventWriter Events);

    private sealed class ConstructionFailedException(Exception cause) : Exception(cause.Message, cause);
    private sealed class ConstructionReadyException : Exception { }
    private sealed class InvalidReceiptException(string message) : InvalidOperationException(message);
}

internal sealed class GoalCreationIntentDeferredException(Exception cause) : Exception(cause.Message, cause);
