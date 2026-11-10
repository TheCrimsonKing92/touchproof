namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAcceptanceLeaseReceipts
{
    internal static void Replay(ConductorDriver driver,
        ConductorParallelAcceptanceAttempt attempt, List<string> changedGoalLines)
    {
        foreach (var receipt in driver.ParallelAcceptanceAttemptCoordinator.TakePendingLeaseReceipts(attempt))
        {
            changedGoalLines.Add(receipt);
        }
    }
}
