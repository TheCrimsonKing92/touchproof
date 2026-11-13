using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsMergeEvidenceDecisionPersistence(ITestOutputHelper output)
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData("plain")]
    [Xunit.InlineData("skipped")]
    [Xunit.InlineData("fault")]
    public void MergeEvidenceTerminalization_CheckpointKeepsDecisionAcrossReload(string scenario)
    {
        using var fixture = new ConductorDecisionPersistenceLoopFixture(output);
        fixture.RunMergeEvidence(scenario);
        fixture.AssertStoredDecision(ConductorDecisionPersistenceLoopFixture.TerminalizationMessage, 1);
        Assert.True(fixture.TerminalizedListObserved);
        Assert.Equal(GoalStatus.Completed, fixture.StoredGoal().Status);
        Assert.Equal(0, fixture.DriverCalls);
        if (scenario == "skipped") Assert.Contains(GoalSnapshotSaveDisposition.Skipped, fixture.SaveDispositions);
        if (scenario == "fault") Assert.True(fixture.SweepFaultObserved);
    }
}
