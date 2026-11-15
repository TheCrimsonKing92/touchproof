using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable enum values only.
public sealed class WorkerBuildOutcomeValueTests
{
    [Fact]
    public void DurableValues_PreserveExistingLiteralsAndAppendRestoreRefusal()
    {
        Assert.Equal(0, (int)WorkerBuildOutcome.Success);
        Assert.Equal(1, (int)WorkerBuildOutcome.CompilerFailure);
        Assert.Equal(2, (int)WorkerBuildOutcome.CapacityBeforeStart);
        Assert.Equal(3, (int)WorkerBuildOutcome.TimeoutAfterStart);
        Assert.Equal(4, (int)WorkerBuildOutcome.Cancelled);
        Assert.Equal(5, (int)WorkerBuildOutcome.StaleCandidate);
        Assert.Equal(6, (int)WorkerBuildOutcome.Interrupted);
        Assert.Equal(7, (int)WorkerBuildOutcome.ApparatusFailure);
        Assert.True((int)WorkerBuildOutcome.PackageRestoreRefused > 7);
    }
}
