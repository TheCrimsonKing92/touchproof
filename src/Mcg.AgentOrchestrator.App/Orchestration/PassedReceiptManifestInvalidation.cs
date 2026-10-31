using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedReceiptManifestInvalidation
{
    internal static IReadOnlyList<string> CheckAll(IEnumerable<Goal> goals,
        ConductorParallelAcceptanceAttemptCoordinator coordinator, string executionDirectory,
        Func<Goal, string?> currentCandidate)
    {
        var pending = new List<string>();
        foreach (var goal in goals.Where(goal => goal.Status == GoalStatus.Verified))
        {
            // Once queued, neither a disappearing blocker nor a manifest revert restores the receipt.
            if (coordinator.TryReadLatest(goal.Id.Value)?.RegateRequestedAt is not null)
                pending.Add(goal.Id.Value);
            else if (goal.LandingBlocker is not null)
            {
                try
                {
                    if (Check(goal, coordinator, executionDirectory, () => currentCandidate(goal)))
                        pending.Add(goal.Id.Value);
                }
                catch (InvalidOperationException) { /* The replay path retains the hold and reports the failed comparison. */ }
            }
        }
        return pending;
    }

    internal static bool Check(Goal goal, ConductorParallelAcceptanceAttemptCoordinator coordinator,
        string executionDirectory, Func<string?> currentCandidate)
    {
        var attempt = coordinator.TryReadLatest(goal.Id.Value);
        var hold = PassedGateRegateAdmission.Resolve(goal, attempt, currentCandidate());
        if (hold is null) return false;
        string identity;
        try
        {
            identity = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                GoalWorktrees.TryResolve(executionDirectory, goal.Id) ?? executionDirectory,
                attempt!.ScopePaths, coordinator.ProjectHomeDirectory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PASSED_RECEIPT_MANIFEST_CHECK_FAILED goal={goal.Id.Value} exception={exception.GetType().Name}");
            // No comparison is available: retain the hold and never authorize landing.
            throw new InvalidOperationException("Cannot compare the held receipt's effective manifest.", exception);
        }
        if (string.Equals(identity, attempt!.EffectiveManifestIdentity, StringComparison.Ordinal)) return false;
        var detail = $"MANIFEST_REGATE_REQUEST attempt={attempt.AttemptId} candidate={hold.Candidate} " +
            $"prior={attempt.EffectiveManifestIdentity ?? "unknown"} current={identity}; {hold.Describe()}";
        if (!coordinator.TryInvalidatePassedReceipt(goal, attempt.AttemptId, hold.Candidate,
            ConductorEvidenceSupersessionCause.ManifestChanged, detail, currentCandidate)) return false;
        GoalOperationJournal.Completed(executionDirectory, goal, "conductor:manifest-regate", detail);
        return true;
    }
}
