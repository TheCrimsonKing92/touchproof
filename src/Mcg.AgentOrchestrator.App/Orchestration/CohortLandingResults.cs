using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Both ordinary and recovered cohort landings publish the same tested landing identity.
internal static class CohortLandingResults
{
    internal static LandingResult Create(string goalId, string cohortIdentity, string message,
        string? commitRevision, IReadOnlyList<string> changedFiles) => new(
            goalId,
            goalId[..8],
            new LandingDecision.Promote(),
            $"cohort/{cohortIdentity}",
            MainAdvanced: true,
            message,
            commitRevision,
            changedFiles);

    internal static ConductorLandingReceipt Notification(LandingResult landing, AcceptanceCohortReceipt gate) => new(
        landing.GoalId, landing.ChangedFiles ?? [], landing.MergeCommitSha,
        gate.GateAttemptId, gate.GateLanePlanPath, gate.Identity.ObservedMainRevision);
}
