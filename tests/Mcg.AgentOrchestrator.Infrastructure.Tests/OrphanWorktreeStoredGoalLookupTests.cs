using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private temporary trees, fake metadata and a fixed injected clock.
public sealed class OrphanWorktreeStoredGoalLookupTests
{
    [Xunit.Theory]
    [Xunit.InlineData(9, "recent", 0)]
    [Xunit.InlineData(11, "removed", 1)]
    public void Sweep_StoredCancelled_UsesGraceWindow(int age, string reason, int removed)
    {
        using var fixture = new OrphanMetadataSweepFixture();
        fixture.Metadata = [fixture.Summary("Cancelled")];
        OrphanSweepTestFixture.AgeTree(fixture.Folder, OrphanSweepTestFixture.ReferenceTime.AddMinutes(-age));

        var result = fixture.Sweep(new AgentOrchestratorKernel());

        Assert.Equal(removed, result.RemovedCount);
        Assert.Equal(removed == 0, Directory.Exists(fixture.Folder));
        fixture.Tree.AssertOutcome(fixture.Folder, reason, fixture.Goal);
        Assert.Equal(1, fixture.Reads);
        Assert.Empty(fixture.Tree.Warnings);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Active", "non-terminal-goal")]
    [Xunit.InlineData("unknown-status", "non-terminal-goal")]
    [Xunit.InlineData("missing", "goal-state-unavailable")]
    [Xunit.InlineData("throw", "goal-state-unavailable")]
    public void Sweep_UnavailableOrLiveMetadata_KeepsFolder(string source, string reason)
    {
        using var fixture = new OrphanMetadataSweepFixture();
        if (source == "throw") fixture.Read = () => throw new NotSupportedException("forced read failure");
        else if (source != "missing") fixture.Metadata = [fixture.Summary(source)];

        var result = fixture.Sweep(new AgentOrchestratorKernel());

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(fixture.Folder));
        fixture.Tree.AssertOutcome(fixture.Folder, reason,
            source is "missing" or "throw" ? null : fixture.Goal);
        Assert.Equal(1, fixture.Reads);
        Assert.Empty(fixture.Tree.Deletions);
        Assert.Empty(fixture.Tree.Warnings);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Active", "non-terminal-goal", 0)]
    [Xunit.InlineData("Completed", "removed", 1)]
    public void Sweep_PrefixCollision_AnyLiveGoalProtects(string status, string reason, int removed)
    {
        using var fixture = new OrphanMetadataSweepFixture();
        var other = "F88C7E54" + new string('a', 24);
        fixture.Metadata = [fixture.Summary("Cancelled"), fixture.Summary(status, other)];

        var result = fixture.Sweep();

        Assert.Equal(removed, result.RemovedCount);
        fixture.Tree.AssertOutcome(fixture.Folder, reason,
            status == "Active" ? new GoalId(other) : fixture.Goal);
        Assert.Equal(1, fixture.Reads);
    }

    [Xunit.Fact]
    public void Sweep_MultipleMissingGoals_ReadsOncePerPassAndRetriesNextPass()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        fixture.Tree.AddDirectory("abcdef01");
        fixture.Read = () => throw new NotSupportedException("forced read failure");
        fixture.Sweep();
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(2, fixture.Tree.Outcomes.Count);
        Assert.All(fixture.Tree.Outcomes, item => Assert.Equal("goal-state-unavailable", item.Reason));

        fixture.Tree.Outcomes.Clear();
        fixture.Read = () => [fixture.Summary("Cancelled")];
        var recovered = fixture.Sweep();

        Assert.Equal(2, fixture.Reads);
        Assert.Equal(1, recovered.RemovedCount);
        fixture.Tree.AssertOutcome(fixture.Folder, "removed", fixture.Goal);
    }

    [Xunit.Fact]
    public void Classify_KnownRegisteredAndOtherNames_DoesNotReadMetadata()
    {
        using var fixture = new OrphanMetadataSweepFixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("live", [new(TaskId.New(), "work", AgentRole.Developer)]);
        var reads = 0;
        var eligibility = new OrphanWorktreeSweepEligibility(kernel, new(() => { reads++; return []; }));
        var registered = new HashSet<string> { GoalWorktreeLayout.NormalizePath(fixture.Folder) };

        Assert.Equal("registered", eligibility.Classify(fixture.Folder, registered, OrphanSweepTestFixture.ReferenceTime));
        Assert.Equal("non-terminal-goal", eligibility.Classify(
            Path.Combine(fixture.Root, goal.Id.Value[..8]), new HashSet<string>(), OrphanSweepTestFixture.ReferenceTime));
        var ordinary = fixture.Tree.AddDirectory("ordinary");
        Assert.Equal("eligible", eligibility.Classify(ordinary, new HashSet<string>(), OrphanSweepTestFixture.ReferenceTime));
        Assert.Equal(0, reads);
    }
}
