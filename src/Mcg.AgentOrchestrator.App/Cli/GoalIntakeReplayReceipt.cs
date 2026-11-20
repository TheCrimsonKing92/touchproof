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
