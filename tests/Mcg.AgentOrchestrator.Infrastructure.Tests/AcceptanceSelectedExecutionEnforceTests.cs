using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: injected candidate data and dispatch checks only.
public sealed class AcceptanceSelectedExecutionEnforceTests
{
    [Fact]
    public void ResolvedPlan_NarrowsClassesAndRecordsTypedSkips()
    {
        var session = Session(AcceptanceSelectedExecutionMode.Enforce);
        var observation = Observe(session);
        var evidence = Assert.IsType<AcceptanceSelectedExecutionRecord>(observation.SelectedExecution);
        Assert.True(session.Applied);
        Assert.Equal(new[] { "Balanced 1", "Chaos gate", "Residual" }, observation.Scheduled);
        var skipped = Assert.Single(observation.Skipped);
        Assert.Equal("Balanced 2", skipped.Name);
        Assert.Equal(AcceptanceGateLaneSkip.LaneNotSelected, skipped.Reason);
        var lane = Assert.Single(evidence.Lanes, item => item.Name == "Balanced 1");
        var check = Assert.Single(BuildChecks(session.Transform(Lanes)), item => item.Name == "infrastructure tests: Balanced 1");
        Assert.True(check.IsResolvedInfrastructureLane);
        Assert.Equal(new[] { "--filter", lane.Filter! }, check.Arguments);
        Assert.Equal(new[] { "--filter-class", "Candidate.A", "--filter-class", "Candidate.Guard",
            "--filter-not-trait", "Category=External", "--filter-not-class", "Candidate.Excluded" },
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter!).ToArray());
        Assert.Equal(new[] { "Candidate.B" }, lane.SkippedClasses);
        Assert.Equal(AcceptanceGateLaneSkip.ClassNotSelected, lane.ClassSkipReason);
        Assert.Equal(Lanes[2].Filter, evidence.Lanes.Single(item => item.Name == "Chaos gate").Filter);
        Assert.Equal(Lanes[3].Filter, evidence.Lanes.Single(item => item.Name == "Residual").Filter);
        Assert.Equal(new[] { "Candidate.Guard" }, evidence.GuardClasses);
        Assert.Equal(new[] { "Chaos gate" }, evidence.FloorLanes);
        Assert.Equal(3, evidence.ExecutedClassCount);
        Assert.Equal(2, evidence.SkippedClassCount);
        using var json = JsonDocument.Parse(observation.ToJson());
        Assert.Equal("Enforce", json.RootElement.GetProperty("selectedExecution").GetProperty("mode").GetString());
    }

    [Fact]
    public void OverlappingMembership_RetainsEachLaneAndCountsDistinctClasses()
    {
        var observation = Observe(Session(AcceptanceSelectedExecutionMode.Enforce),
            [.. Lanes, new("Overlap", "FullyQualifiedName=Candidate.A")]);
        Assert.Contains("Overlap", observation.Scheduled);
        Assert.Equal(3, observation.SelectedExecution!.ExecutedClassCount);
        Assert.Equal(2, observation.SelectedExecution.SkippedClassCount);
    }

    [Fact]
    public void SelectionOutsideOriginalLane_DoesNotBroadenMembership()
    {
        var lanes = new[] { new AcceptanceTestLane("A only", "FullyQualifiedName=Candidate.A"), Lanes[2] };
        var input = new AcceptanceSelectedExecutionInput(ReverseDependencyTestSelection.Resolved(["Floor"]), Classes, []);
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            "candidate", Changes, read: (_, _) => input);
        var executed = session.LaneTransform!(lanes);
        Assert.True(session.Applied);
        Assert.Equal(new[] { "Chaos gate" }, executed.Select(lane => lane.Name));
        Assert.Equal(Lanes[2].Filter, Assert.Single(executed).Filter);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Candidate.MissingGuard")]
    public void UnmappedClass_FallsBackBeforeNarrowing(string selector)
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([selector]),
            Lanes, Classes, ["Chaos gate"], []);
        Assert.False(plan.HasPlan);
        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, plan.Outcome);
        Assert.Equal("UnmappedTestClass: " + selector, plan.FullGateReason);
        Assert.Equal(Lanes, plan.ExecutionLanes);
    }

    [Fact]
    public void NarrowedLane_AllowsEmptyTraitExcludedSelection()
    {
        var session = Session(AcceptanceSelectedExecutionMode.Enforce);
        var lanes = session.LaneTransform!(Lanes);
        Assert.True(lanes.Single(lane => lane.Name == "Balanced 1").AllowsEmptySelection);
    }

    [Fact]
    public void InvalidFilter_FallsBackAsUnreadableBeforeDispatch()
    {
        var session = Session(AcceptanceSelectedExecutionMode.Enforce);
        var lanes = new[] { new AcceptanceTestLane("Unsupported", "Unknown=Value") };
        Assert.Same(lanes, session.LaneTransform!(lanes));
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;
        Assert.False(session.Applied);
        Assert.Equal("Unreadable", record.Outcome);
        Assert.StartsWith("InvalidOperationException:", record.FullGateReason!);
    }
}
