using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: all state is local, with in-memory removals and no background producers.
public sealed class AcceptanceCohortWorkflowTestsDrainStateDescription
{
    [Fact]
    public void UndrainedBound_StoresCallbackTextOnceAndSkipsRemovals()
    {
        const string description = "key=owner-a members=goal-a,goal-b started_at=2026-11-15T12:00:00Z";
        var callbackCount = 0;
        var removed = new List<string>();

        var result = AcceptanceCohortWorkflowBackgroundSupport.RemoveAfterObservedDrain(
            () => false, "ActiveRootCount == 0", TimeSpan.FromMilliseconds(1),
            () => { callbackCount++; return description; },
            () => removed.Add("trx"), () => removed.Add("repo"));

        Assert.False(result.Drained);
        Assert.Equal(description, result.StateDescription);
        Assert.Equal(1, callbackCount);
        Assert.Empty(removed);
        for (var assertion = 0; assertion < 2; assertion++)
        {
            var failure = Record.Exception(result.AssertDrained);
            Assert.NotNull(failure);
            Assert.Contains("Background cohort gate did not drain: ActiveRootCount == 0.", failure.Message, StringComparison.Ordinal);
            Assert.Contains($"State at bound: {description}", failure.Message, StringComparison.Ordinal);
        }
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void ObservedDrain_RunsRemovalsWithoutInvokingCallback()
    {
        var callbackCount = 0;
        var removed = new List<string>();

        var result = AcceptanceCohortWorkflowBackgroundSupport.RemoveAfterObservedDrain(
            () => true, "ActiveRootCount == 0", TimeSpan.FromSeconds(10),
            () => { callbackCount++; return "must not be captured"; },
            () => removed.Add("trx"), () => removed.Add("repo"));

        Assert.True(result.Drained);
        Assert.Null(result.StateDescription);
        Assert.Equal(new[] { "trx", "repo" }, removed);
        Assert.Null(Record.Exception(result.AssertDrained));
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public void UndrainedBoundWithoutCallback_KeepsExistingFailureText()
    {
        var result = AcceptanceCohortWorkflowBackgroundSupport.RemoveAfterObservedDrain(
            () => false, "ActiveRootCount == 0", TimeSpan.FromMilliseconds(1));

        Assert.False(result.Drained);
        Assert.Null(result.StateDescription);
        var failure = Record.Exception(result.AssertDrained);
        Assert.NotNull(failure);
        Assert.Equal("Background cohort gate did not drain: ActiveRootCount == 0.", failure.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UndrainedBlankDescription_KeepsExistingFailureText(string? description)
    {
        var result = AcceptanceCohortWorkflowBackgroundSupport.RemoveAfterObservedDrain(
            () => false, "ActiveRootCount == 0", TimeSpan.FromMilliseconds(1), () => description);

        Assert.Equal(description, result.StateDescription);
        var failure = Record.Exception(result.AssertDrained);
        Assert.NotNull(failure);
        Assert.Equal("Background cohort gate did not drain: ActiveRootCount == 0.", failure.Message);
    }

    [Fact]
    public void ActiveRootDescription_NamesKeyMembersAndStartForEveryRoot()
    {
        var snapshot = new ConductorAcceptanceCapacitySnapshot(
        [
            new("owner-a", new HashSet<string>(StringComparer.Ordinal) { "goal-b", "goal-a" },
                new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero)),
            new("owner-c", new HashSet<string>(StringComparer.Ordinal) { "goal-e", "goal-c", "goal-d" },
                new DateTimeOffset(2026, 11, 15, 13, 30, 0, TimeSpan.FromHours(1)))
        ]);

        var description = AcceptanceCohortWorkflowTestsBackgroundGateMembership.DescribeActiveRoots(
            snapshot, TimeSpan.FromMilliseconds(1250));

        Assert.Contains("elapsed_since_release_ms=1250 active_roots=2", description, StringComparison.Ordinal);
        Assert.Contains("[key=owner-a members=goal-a,goal-b started_at=2026-11-15T12:00:00.0000000+00:00]",
            description, StringComparison.Ordinal);
        Assert.Contains("[key=owner-c members=goal-c,goal-d,goal-e started_at=2026-11-15T13:30:00.0000000+01:00]",
            description, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveRootDescription_SaysWhenSnapshotHasNoActiveRoots()
    {
        var description = AcceptanceCohortWorkflowTestsBackgroundGateMembership.DescribeActiveRoots(
            new ConductorAcceptanceCapacitySnapshot([]), TimeSpan.FromMilliseconds(1250));

        Assert.Contains("elapsed_since_release_ms=1250 active_roots=0", description, StringComparison.Ordinal);
        Assert.Contains("no active roots", description, StringComparison.Ordinal);
    }
}
