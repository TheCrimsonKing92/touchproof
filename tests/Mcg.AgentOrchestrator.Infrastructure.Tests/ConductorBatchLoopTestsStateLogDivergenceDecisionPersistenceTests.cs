using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsStateLogDivergenceDecisionPersistenceTests(ITestOutputHelper output)
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData("plain")]
    [Xunit.InlineData("conflict")]
    [Xunit.InlineData("idle")]
    public void Readmission_AfterCheckpointAndReload_HasNoLostDecision(string scenario)
    {
        using var fixture = new ConductorDecisionPersistenceLoopFixture(output);
        fixture.RunReadmission(scenario);

        AssertDecisionComparison(fixture, ConductorDecisionPersistenceLoopFixture.ReadmissionMessage);
    }

    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    [Xunit.InlineData("plain")]
    [Xunit.InlineData("skipped")]
    [Xunit.InlineData("fault")]
    public void MergeEvidence_AfterCheckpointAndReload_HasNoLostDecision(string scenario)
    {
        using var fixture = new ConductorDecisionPersistenceLoopFixture(output);
        fixture.RunMergeEvidence(scenario);
        Assert.True(fixture.TerminalizedListObserved);
        Assert.Equal(GoalStatus.Completed, fixture.StoredGoal().Status);

        AssertDecisionComparison(fixture, ConductorDecisionPersistenceLoopFixture.TerminalizationMessage);
    }

    private static void AssertDecisionComparison(
        ConductorDecisionPersistenceLoopFixture fixture, string message)
    {
        var stored = fixture.StoredGoal().Timeline.Select(StateLogEntry.FromProgressEvent).ToArray();
        var logged = fixture.ReadLog();
        var decision = Assert.Single(logged.Where(line =>
            line.Entry.Message.StartsWith(message, StringComparison.Ordinal)));
        Assert.Equal(nameof(ProgressKind.GoalPolicyDecision), decision.Entry.Kind);
        Assert.Null(decision.Entry.TaskId);
        Assert.Equal(decision.Entry, Assert.Single(stored.Where(entry =>
            entry.Message.StartsWith(message, StringComparison.Ordinal))));

        var report = StateLogDivergenceComparer.Compare(stored, logged);
        var decisionItems = report.Items.Where(item =>
            item.Entry.Message.StartsWith(message, StringComparison.Ordinal)).ToArray();
        Assert.Equal(0, decisionItems.Count(item => item.Class == StateLogDivergenceClass.Lost));
        Assert.Empty(decisionItems);

        // Removing only this stored event must expose it as Lost, rather than exempting it.
        var missing = StateLogDivergenceComparer.Compare(
            stored.Where(entry => entry != decision.Entry), logged);
        var lost = Assert.Single(missing.Items.Where(item => item.Entry == decision.Entry));
        Assert.Equal(StateLogDivergenceClass.Lost, lost.Class);
        Assert.Equal(decision.Cursor, lost.Cursor);
    }
}
