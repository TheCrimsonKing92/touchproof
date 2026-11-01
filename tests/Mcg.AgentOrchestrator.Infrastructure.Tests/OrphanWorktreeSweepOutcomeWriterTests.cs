using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository, hooks and writer; no ambient Console mutation.
public sealed class OrphanWorktreeSweepOutcomeWriterTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("registered")]
    [Xunit.InlineData("non-terminal-goal")]
    [Xunit.InlineData("recent")]
    [Xunit.InlineData("lock-unavailable")]
    [Xunit.InlineData("listing-failed")]
    public void Write_RoutineOrAlreadyWarnedReason_WritesNothing(string reason)
    {
        using var writer = new StringWriter();
        var outcome = new OrphanWorktreeSweepOutcome("worktree", null, reason, 42, "orchestrator", "test");

        OrphanWorktreeSweepOutcome.Write(outcome, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Xunit.Theory]
    [Xunit.InlineData("removed")]
    [Xunit.InlineData("removal-failed")]
    [Xunit.InlineData("freshness-unknown")]
    [Xunit.InlineData("goal-state-unavailable")]
    [Xunit.InlineData("backoff")]
    [Xunit.InlineData("future-reason")]
    [Xunit.InlineData("Registered")]
    [Xunit.InlineData("registered ")]
    public void Write_OtherReason_WritesExactlyOneJsonLine(string reason)
    {
        using var writer = new StringWriter();
        var outcome = new OrphanWorktreeSweepOutcome("worktree", GoalId.New(), reason, 42,
            "orchestrator", "test");

        OrphanWorktreeSweepOutcome.Write(outcome, writer);

        Assert.Equal("orphan-sweep-outcome " + JsonSerializer.Serialize(outcome) + writer.NewLine,
            writer.ToString());
    }

    [Xunit.Fact]
    public void Sweep_RegisteredAndEligibleWorktrees_WritesOnlyRemoval()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Registered", [new(TaskId.New(), "Work", AgentRole.Developer)]);
            kernel.CancelGoal(goal.Id, "Registration is independently protective.");
            var registered = GoalWorktrees.Ensure(repo, goal.Id);
            OrphanSweepTestFixture.AgeTree(registered, OrphanSweepTestFixture.ReferenceTime.AddHours(-1));
            var fixture = new OrphanSweepTestFixture(repo);
            var orphan = fixture.AddDirectory("unregistered-orphan");
            Assert.True(File.Exists(Path.Combine(registered, ".git")));
            Assert.Equal(2, Directory.GetDirectories(Path.Combine(repo, GoalWorktrees.DirectoryName)).Length);
            using var writer = new StringWriter();
            var hooks = fixture.Hooks with
            {
                OrphanSweepOutcomeSink = outcome =>
                {
                    fixture.Outcomes.Add(outcome);
                    OrphanWorktreeSweepOutcome.Write(outcome, writer);
                }
            };

            var result = fixture.Sweep(kernel, hooks);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(orphan, Assert.Single(fixture.Deletions));
            Assert.True(Directory.Exists(registered));
            Assert.False(Directory.Exists(orphan));
            Assert.Empty(fixture.Warnings);
            Assert.Equal(2, fixture.Outcomes.Count);
            fixture.AssertOutcome(registered, "registered", goal.Id);
            var removal = fixture.AssertOutcome(orphan, "removed");
            Assert.Equal("orphan-sweep-outcome " + JsonSerializer.Serialize(removal) + writer.NewLine,
                writer.ToString());
        }
        finally { DeleteDirectory(repo); }
    }
}
