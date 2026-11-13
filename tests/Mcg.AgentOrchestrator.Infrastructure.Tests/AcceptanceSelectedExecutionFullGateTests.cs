using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: the selection and candidate inventory are injected.
public sealed class AcceptanceSelectedExecutionFullGateTests
{
    [Theory]
    [InlineData("Unreadable")]
    [InlineData("Unavailable")]
    [InlineData("Abandoned")]
    public void NonResolvedSelection_RunsFullGateAndRecordsReason(string outcome)
    {
        var selection = new ReverseDependencyTestSelection(Enum.Parse<ReverseDependencySelectionOutcome>(outcome), [], "reason-for-" + outcome);
        var session = Session(AcceptanceSelectedExecutionMode.Enforce, selection);
        var actual = session.LaneTransform!(Lanes);
        Assert.Same(Lanes, actual);
        Assert.False(session.Applied);
        var baseline = Observe(Session(AcceptanceSelectedExecutionMode.Off));
        var observation = Observe(session);
        Assert.Equal(baseline.Scheduled, observation.Scheduled);
        Assert.Empty(observation.Skipped);
        var record = observation.SelectedExecution!;
        Assert.Equal("Enforce", record.Mode);
        Assert.Equal("full-gate", record.Decision);
        Assert.Equal(outcome, record.Outcome);
        Assert.Equal("reason-for-" + outcome, record.FullGateReason);
        Assert.Equal(5, record.ExecutedClassCount);
        Assert.Equal(0, record.SkippedClassCount);
        Assert.False(record.StructuralCoverageScoped);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(28)]
    [InlineData(64)]
    public void ResolvedSelection_ManyChangedSources_StillProducesPlan(int sourceFiles)
    {
        var changes = Enumerable.Range(0, sourceFiles).Select(index => "src/Changed" + index + ".cs").ToArray();
        var session = Session(AcceptanceSelectedExecutionMode.Enforce, changes: changes);
        var observation = Observe(session);
        Assert.True(session.Applied);
        Assert.Equal("plan", observation.SelectedExecution!.Decision);
        Assert.Null(observation.SelectedExecution.FullGateReason);
        Assert.Equal(3, observation.SelectedExecution.ExecutedClassCount);
        Assert.Contains(observation.Skipped, lane => lane.Name == "Balanced 2");
    }

    [Fact]
    public void MissingCandidateChanges_DoesNotInvokeReaderAndRunsFullGate()
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            "candidate", null, read: (_, _) => throw new Xunit.Sdk.XunitException("Reader must not run."));
        Assert.Same(Lanes, session.LaneTransform!(Lanes));
        var observation = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], []));
        Assert.Equal("Unavailable", observation.SelectedExecution!.Outcome);
        Assert.Equal("Candidate changes are unavailable.", observation.SelectedExecution.FullGateReason);
    }

    [Fact]
    public void AttributionScope_PreservesFullFiltersAndRecordsReason()
    {
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce,
            "candidate", Changes, attributionScoped: true,
            read: (_, _) => throw new Xunit.Sdk.XunitException("Reader must not run."));
        Assert.Same(Lanes, session.LaneTransform!(Lanes));
        var record = session.Observe(new(AcceptanceGateLanePlan.CurrentSchemaVersion, "attempt", [], [])).SelectedExecution!;
        Assert.Equal("Attribution-scoped attempt.", record.FullGateReason);
        Assert.False(session.Applied);
    }

    [Fact]
    public void NextAttempt_RecomputesCandidateSelection()
    {
        var calls = 0;
        AcceptanceSelectedExecutionInput Read(string _, IReadOnlyList<string> __) =>
            new(ReverseDependencyTestSelection.Resolved([++calls == 1 ? "A" : "X"]), Classes, []);
        var first = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, "candidate", Changes, read: Read);
        var second = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, "candidate", Changes, read: Read);
        Assert.Contains(first.LaneTransform!(Lanes), lane => lane.Name == "Balanced 1");
        Assert.DoesNotContain(second.LaneTransform!(Lanes), lane => lane.Name == "Balanced 1");
        Assert.Equal(2, calls);
    }

    [Fact]
    public void EmptyInventory_ResolvedSelection_FallsBackToFullGate()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            Lanes, [], ["Chaos gate"], []);
        Assert.False(plan.HasPlan);
        Assert.Equal(ReverseDependencySelectionOutcome.Unavailable, plan.Outcome);
        Assert.Equal("Candidate test-class inventory is unavailable.", plan.FullGateReason);
        Assert.Same(Lanes, plan.ExecutionLanes);
    }
}
