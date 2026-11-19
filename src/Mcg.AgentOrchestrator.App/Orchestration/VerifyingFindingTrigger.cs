using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record VerifyingFindingTrigger(
    TaskSpec TriggeringTask,
    string Finding,
    IReadOnlyList<string> SuppressedFindings,
    TaskSpec? TargetTask,
    bool RequiresCommittedTarget = false,
    IReadOnlyList<ReviewFinding>? DeveloperOwnedFindings = null)
{
    internal static VerifyingFindingTrigger? TryCreate(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Failed)
        {
            return null;
        }

        if (task.LastVerification is { } contractRecord &&
            !WorkerResultBlockers.AssessSubscriptionVerification(task, contractRecord).AllowsCompletion)
            return null;

        if (task.LastVerification is { } latestVerification &&
            !VerifyingFindingCurrency.IsCurrent(goal, task, latestVerification))
        {
            return null;
        }

        string blocker;
        if (task.RequiredRole == AgentRole.Reviewer)
        {
            var hasNeedsWork = WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out blocker,
                out var suppressedFindings);
            var hasBlockedAtCap = WorkerResultBlockers.TryFindUnsuppressedBlockedAtCapVerdict(
                task.LastVerification,
                goal.EffectiveAcceptanceCriteriaCorrections,
                out var capBlocker,
                out var capSuppressedFindings);
            if (hasBlockedAtCap && task.LastDispatch?.ReviewRetryCap is not { IsAtCap: true })
            {
                return null;
            }

            if (hasNeedsWork || hasBlockedAtCap)
            {
                return new VerifyingFindingTrigger(
                    task,
                    hasBlockedAtCap ? capBlocker : blocker,
                    hasBlockedAtCap ? capSuppressedFindings : suppressedFindings,
                    null);
            }
        }

        if (task.RequiredRole != AgentRole.Tester ||
            !WorkerResultBlockers.TryGetTestsStatus(task.LastVerification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Fail ||
            !WorkerResultBlockers.TryFindHardFailureBlocker(task.LastVerification, out blocker))
        {
            return null;
        }

        var upstreamDeveloper = goal.Tasks
            .TakeWhile(t => t.Id != task.Id)
            .LastOrDefault(t => t.RequiredRole == AgentRole.Developer && VerifyingFindingCurrency.HasCommittedOutput(t));
        if (upstreamDeveloper is null)
        {
            return null;
        }

        return new VerifyingFindingTrigger(task, blocker, [], upstreamDeveloper);
    }
}
