using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owned by the batch-loop instance: normal and recovered landings share one process-lifetime decision.
internal sealed class LandingRelaunchDecisions(
    bool selfRelaunchEnabled,
    bool selfRelaunchAvailable,
    PostLandingCanaryCoordinator? canary)
{
    private readonly HashSet<string> _handledGoals = new(StringComparer.Ordinal);

    internal bool Contains(string goalId) => _handledGoals.Contains(goalId);

    internal void Handle(
        int tick,
        ConductorLandingReceipt receipt,
        Action scheduleRelaunch,
        Action<Task<PostLandingCanaryDisposition>> trackCanary,
        Action<string> emit,
        bool changedFilesAvailable = true)
    {
        if (!_handledGoals.Add(receipt.GoalId)) return;
        var decision = RepositoryChangeClassifier.DecideConductorRelaunch(receipt.ChangedFiles);
        if (!changedFilesAvailable)
            decision = decision with { Required = false, Classification = "changed-files-unavailable" };
        if (decision.Required && selfRelaunchEnabled && selfRelaunchAvailable)
        {
            scheduleRelaunch();
            emit($"LOOP_RELAUNCH_SCHEDULED tick={tick} goal={receipt.GoalId} " +
                $"changedFiles={receipt.ChangedFiles.Count} coalesced=true");
        }
        else
        {
            var reason = decision.Required
                ? (selfRelaunchEnabled ? "self-relaunch-unavailable" : "self-relaunch-disabled") +
                  $"+{decision.Classification}"
                : decision.Classification;
            emit($"LOOP_RELAUNCH_NOT_REQUIRED tick={tick} goal={receipt.GoalId} " +
                $"changedFiles={receipt.ChangedFiles.Count} classification={reason}");
        }
        if (canary is not null) trackCanary(canary.LaunchLandingAsync(receipt));
    }
}
