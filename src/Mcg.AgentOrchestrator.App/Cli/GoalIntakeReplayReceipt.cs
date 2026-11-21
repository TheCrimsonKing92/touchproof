using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalIntakeReplayReceipt
{
    public static void Print(
        GoalIntakeRequestRecord record,
        GoalIntakeRequestDescriptor request,
        IOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        bool summary,
        ref Goal? currentGoal)
    {
        if (!summary) GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(record);
        if (record.State == GoalIntakeRequestStates.Created)
        {
            var replayKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            currentGoal = replayKernel.Goals.FirstOrDefault(goal =>
                goal.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
            if (currentGoal is null)
            {
                throw new InvalidOperationException(
                    $"GOAL_INTAKE_RECEIPT_INVALID requestKey={request.RequestKey} reason=created-goal-missing");
            }
            if (request.RequestedEpicId is { } epicId)
            {
                var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
                var goalId = currentGoal.Id.Value;
                if (portfolio.GetGoalMembershipAsync(goalId).GetAwaiter().GetResult() is null)
                    EpicAtCreation.AssignAfterCommit(workspace, PortfolioMemberKind.Goal, goalId, epicId);
            }
            if (summary)
                GoalCreationSummaryView.Print(currentGoal, "replayed", workspace.PortfolioStorePath);
            else
                GoalTextView.PrintGoal(currentGoal);
            return;
        }
        if (summary) GoalCreationSummaryView.PrintProgress(record);
        if (record.State == GoalIntakeRequestStates.Failed)
        {
            throw new InvalidOperationException(
                $"GOAL_INTAKE_REPLAY_FAILED requestKey={request.RequestKey} " +
                $"failureCode={record.FailureCode} detail={record.FailureDetail}");
        }
    }
}

internal sealed class GoalIntakeCommandReceipt
{
    private readonly GoalIntakeRequestStore? _store;
    private readonly OrchestratorWorkspace _workspace;
    private readonly bool _showSummary;
    private readonly bool _useContinuation;
    private readonly bool _isReplay;
    private readonly bool _resumeContinuation;

    internal GoalIntakeCommandReceipt(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents)
    {
        _workspace = workspace;
        _showSummary = GoalCreationSummaryView.IsRequested(args);
        _useContinuation = CliPersistentStateRunner.IsBacklogIntakeGoalCreationCommand(args) &&
            args.Contains("--create-goal", StringComparer.OrdinalIgnoreCase) &&
            args.Contains("--force-reclaim", StringComparer.OrdinalIgnoreCase);
        var request = GoalIntakeRequestMapper.Map(args, workspace, agents);
        if (request is null)
            return;

        Request = request;
        IsBacklogRequest = request.Mode.StartsWith("backlog-", StringComparison.Ordinal);
        _store = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        var reservation = request.Reserve(_store);
        Record = reservation.Record;
        _isReplay = reservation.Kind == GoalIntakeReservationKind.Replay;
        _resumeContinuation = _useContinuation && _isReplay &&
            reservation.Record.State == GoalIntakeRequestStates.StillCommitting &&
            new BacklogIntakeRecordStore(workspace.SqliteStatePath).HasPendingContinuation(request.RequestKey);
    }

    internal GoalIntakeRequestRecord? Record { get; private set; }
    internal GoalIntakeRequestDescriptor? Request { get; }
    internal bool IsBacklogRequest { get; }

    internal BacklogIntakeContinuationHost? ContinuationHost(IOrchestratorStateOutboxRepository repository) =>
        _useContinuation ? new(repository, Record, RequestReplay: _resumeContinuation) : null;

    internal bool TryReplay(IOrchestratorStateOutboxRepository repository, AgentOrchestratorKernel kernel, ref Goal? goal)
    {
        if (!_isReplay || _resumeContinuation || Record is not { } record)
            return false;

        if (_useContinuation && record.GoalId is { } successorId &&
            kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value == successorId)?.SourceBacklogItemId is { } itemId &&
            new SourceBacklogClaimStore(_workspace.SqliteStatePath).ListContinuationLineage(itemId)
                .SingleOrDefault(row => row.SuccessorGoalId == successorId) is { } lineage)
        {
            CliPersistentStateRunner.DeliverGoalCreationSideEffects(repository, _workspace, kernel, new GoalId(successorId));
            BacklogIntakeContinuation.PrintBacklogContinuationLineage(lineage);
        }
        GoalIntakeReplayReceipt.Print(record, Request!, repository, _workspace, _showSummary, ref goal);
        return true;
    }

    internal bool TryPrintContinuationResult(IOrchestratorStateRepository repository, ref Goal? goal)
    {
        if (!_useContinuation || Record is not { } record || _store!.Get(record.RequestKey) is not { } receipt ||
            (!_resumeContinuation && receipt.State != GoalIntakeRequestStates.Created))
            return false;

        if (receipt.State != GoalIntakeRequestStates.Created)
            GoalIntakeReplayReceipt.Print(receipt, Request!, repository, _workspace, _showSummary, ref goal);
        else if (_showSummary && goal is not null && goal.Id.Value == receipt.GoalId)
            GoalCreationSummaryView.Print(goal, _resumeContinuation ? "replayed" : "created", _workspace.PortfolioStorePath);
        else
            GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(receipt);
        return true;
    }

    internal void MarkCreated(string goalId)
    {
        if (Record is { } record)
            Record = _store!.MarkCreated(record.RequestKey, record.Fingerprint, goalId);
    }

    internal void ReportProgress()
    {
        if (Record is not null && !_showSummary)
            GoalIntakeReceiptTextView.PrintGoalIntakeReceipt(Record);
    }

    internal void MarkFailed(Exception exception)
    {
        if (Record is not { } record)
            return;
        if (_useContinuation && (_store!.Get(record.RequestKey)?.State == GoalIntakeRequestStates.Created ||
            new BacklogIntakeRecordStore(_workspace.SqliteStatePath).HasPendingContinuation(record.RequestKey)))
            return;

        var firstToken = exception.Message.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var code = firstToken is not null && firstToken.Length <= 100 &&
            firstToken.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_')
            ? firstToken
            : exception is ArgumentException ? "GOAL_INTAKE_ARGUMENT_INVALID" : "GOAL_INTAKE_CREATE_FAILED";
        _ = _store!.MarkFailed(record.RequestKey, record.Fingerprint, code, exception.Message);
    }
}
