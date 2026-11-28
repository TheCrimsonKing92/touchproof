using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionProjectFixture;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

// Parallel-safe: the lane plans and inventories are isolated in memory.
public sealed class AcceptanceSelectedExecutionExecutedCountTests
{
    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 11)]
    public void Observe_WholeProjectCheck_CountsDistinctClassesAcrossScheduledLanes(bool shadow, int executedCount)
    {
        var mode = shadow ? AcceptanceSelectedExecutionMode.Shadow : AcceptanceSelectedExecutionMode.Enforce;
        var session = new AcceptanceSelectedExecutionSession(mode, "in-memory candidate", AcceptanceSelectedExecutionFixture.Changes,
            read: (_, _) => new(ReverseDependencyTestSelection.Resolved(["A", "Candidate.CliReached"]),
                AcceptanceSelectedExecutionFixture.Classes, ["Candidate.Guard"],
                ProjectClasses: project => project == OwnerConsoleTestsProject
                    ? [.. Inventories[1].Classes, new("Candidate.A", null)] : Inventories[0].Classes,
                SupportSelections: [new("Support.cs", 0, 0, false, false, true,
                    [OwnerConsoleTestsProject], true, ["support reach unavailable"])]));

        var observation = Observe(session);

        var record = Assert.IsType<AcceptanceSelectedExecutionRecord>(observation.SelectedExecution);
        Assert.Equal(!shadow, record.Applied);
        var owner = Assert.Single(record.Lanes, lane => lane.Name == "owner console tests");
        Assert.Equal("retained", owner.Disposition);
        Assert.Null(owner.Filter);
        Assert.Equal(4, owner.SelectedClasses.Count);
        Assert.Contains("Candidate.A", owner.SelectedClasses);
        Assert.Contains(owner.Name, observation.Scheduled);
        Assert.Equal("narrowed", Assert.Single(record.Lanes, lane => lane.Name == "cli tests").Disposition);
        var actual = record.Lanes.Where(lane => observation.Scheduled.Contains(lane.Name))
            .SelectMany(lane => shadow ? lane.SelectedClasses.Concat(lane.SkippedClasses) : lane.SelectedClasses)
            .Distinct(StringComparer.Ordinal).Count();
        var proposed = record.Lanes.SelectMany(lane => lane.SelectedClasses).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(executedCount, actual);
        Assert.Equal(7, proposed);
        Assert.Equal(actual, record.ExecutedClassCount);
        Assert.Equal(proposed, record.ProposedExecutedClassCount);
    }
}
