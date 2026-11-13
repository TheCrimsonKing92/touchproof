using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsReadmissionDecisionPersistence(ITestOutputHelper output)
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("plain")]
    [Xunit.InlineData("conflict")]
    [Xunit.InlineData("idle")]
    public void StateChangedReadmission_CheckpointStoresDecisionInSameTick(string scenario)
    {
        using var fixture = new ConductorDecisionPersistenceLoopFixture(output);
        fixture.RunReadmission(scenario);
        fixture.AssertStoredDecision(ConductorDecisionPersistenceLoopFixture.ReadmissionMessage, 2);
        if (scenario == "idle")
        {
            Assert.Equal(GoalStatus.Parked, fixture.StoredGoal().Status);
            Assert.Equal(0, fixture.DriverCalls);
        }
        else
        {
            Assert.Equal(1, fixture.DriverCalls);
        }
        if (scenario == "conflict")
        {
            Assert.Equal(1, fixture.CriticalAttempts);
            Assert.Equal(0, fixture.CriticalConflicts);
            Assert.Contains(GoalSnapshotSaveDisposition.Merged, fixture.SaveDispositions);
        }
    }
}
