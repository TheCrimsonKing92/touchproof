using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreReviewEvidenceBuildSlotsBusyException(GoalId goalId, WorkerBuildCapacitySnapshot snapshot)
    : Exception($"Pre-review evidence build capacity busy for {goalId.Value}; " +
        $"unheld={snapshot.UnheldPermits}/{snapshot.TotalPermits}; waiting={snapshot.WaitingTickets}.")
{
    internal GoalId GoalId { get; } = goalId;
    internal int UnheldPermits { get; } = snapshot.UnheldPermits;
    internal int TotalPermits { get; } = snapshot.TotalPermits;
    internal int WaitingTickets { get; } = snapshot.WaitingTickets;
}
