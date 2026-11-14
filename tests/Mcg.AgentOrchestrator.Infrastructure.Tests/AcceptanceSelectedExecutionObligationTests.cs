using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;

// Parallel-safe: injected inventory only; no processes or shared state.
public sealed class AcceptanceSelectedExecutionObligationTests
{
    internal static AcceptanceSelectedExecutionSession WithObligations(IReadOnlyList<string>? selectors,
        AcceptanceSelectedExecutionMode mode = AcceptanceSelectedExecutionMode.Enforce) =>
        new(mode, "candidate", Changes, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(["A"]), Classes, ["Candidate.Guard"], selectors));

    [Theory]
    [InlineData("B")]
    [InlineData("Candidate.B")]
    public void ObligationRetainsDroppedClassWithoutBecomingGuard(string selector)
    {
        var observation = Observe(WithObligations([selector]));
        var record = observation.SelectedExecution!;
        var lane = Assert.Single(record.Lanes, lane => lane.Name == "Balanced 1");
        Assert.Contains("--filter-class", AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter!));
        Assert.Contains("FullyQualifiedName=Candidate.B", lane.Filter!, StringComparison.Ordinal);
        Assert.Contains("Candidate.B", lane.SelectedClasses);
        Assert.DoesNotContain("Candidate.B", lane.SkippedClasses);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
        Assert.Equal(new[] { "Candidate.Guard" }, record.GuardClasses);
        using var json = JsonDocument.Parse(observation.ToJson());
        Assert.Equal(new[] { "Candidate.B" }, json.RootElement
            .GetProperty("selectedExecution").GetProperty("obligationClasses").EnumerateArray().Select(item => item.GetString()));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("FullyQualifiedName~Candidate.B")]
    [InlineData("tests/Package.cs")]
    [InlineData("Candidate.B.Check")]
    [InlineData("../Candidate.B")]
    public void UnresolvedSelectorFallsBackBeforeExecution(string selector)
    {
        var session = WithObligations([selector, "B"]);
        Assert.Same(Lanes, session.Transform(Lanes));
        var record = session.Observe(new("gate-lane-plan.v1", "attempt", [], [])).SelectedExecution!;
        Assert.False(session.Applied);
        Assert.Equal("full-gate", record.Decision);
        Assert.Equal("Abandoned", record.Outcome);
        Assert.Equal("UnresolvedObligationSelector: " + selector, record.FullGateReason);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
    }

    [Fact]
    public void ClassOutsideEveryLaneFallsBack()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["A"]),
            [new("Only A", "FullyQualifiedName=Candidate.A")], Classes, [], [], ["B"]);
        Assert.False(plan.HasPlan);
        Assert.Equal("UnresolvedObligationSelector: B", plan.FullGateReason);
    }

    [Fact]
    public void SharedSimpleNameRetainsAllMatchesWithExistingCaseRules()
    {
        AcceptanceTestClassDescriptor[] classes = [new("One.B", null), new("Two.B", null), new("One.Nested+B", null)];
        AcceptanceTestLane[] lanes = [new("Both", "FullyQualifiedName~One|FullyQualifiedName~Two")];
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["B", "One.Nested+B", "B"]);
        Assert.True(plan.HasPlan);
        Assert.Equal(new[] { "One.B", "One.Nested+B", "Two.B" }, plan.ObligationClasses);
        Assert.Equal(plan.ObligationClasses, Assert.Single(plan.Lanes).SelectedClasses);
        var wrongCase = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["b"]);
        Assert.Equal("UnresolvedObligationSelector: b", wrongCase.FullGateReason);
    }

    [Fact]
    public void AlreadySelectedGuardAndFloorStillRecordObligationProvenance()
    {
        var record = Observe(WithObligations(["Floor", "Guard", "A", "Guard"])).SelectedExecution!;
        Assert.Equal(new[] { "Candidate.A", "Candidate.Floor", "Candidate.Guard" }, record.ObligationClasses);
        Assert.Equal(new[] { "Candidate.Guard" }, record.GuardClasses);
        Assert.Equal(new[] { "Chaos gate" }, record.FloorLanes);
    }

    [Fact]
    public void ShadowRecordsObligationsWithoutChangingExecution()
    {
        var session = WithObligations(["B"], AcceptanceSelectedExecutionMode.Shadow);
        var observation = Observe(session);
        Assert.False(session.Applied);
        Assert.Equal(Lanes.Select(lane => lane.Name), observation.Scheduled);
        Assert.Equal(new[] { "Candidate.B" }, observation.SelectedExecution!.ObligationClasses);
        Assert.Contains("Candidate.B", observation.SelectedExecution.Lanes.Single(lane => lane.Name == "Balanced 1").SelectedClasses);
        Assert.False(observation.SelectedExecution.Applied);
    }

    [Fact]
    public void ShadowUnresolvedSelectorRecordsFullGateWithoutChangingExecution()
    {
        var session = WithObligations(["Missing", "B"], AcceptanceSelectedExecutionMode.Shadow);
        var observation = Observe(session);
        Assert.False(session.Applied);
        Assert.Equal(Lanes.Select(lane => lane.Name), observation.Scheduled);
        Assert.Equal("full-gate", observation.SelectedExecution!.Decision);
        Assert.Equal("UnresolvedObligationSelector: Missing", observation.SelectedExecution.FullGateReason);
        Assert.Equal(new[] { "Candidate.B" }, observation.SelectedExecution.ObligationClasses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyObligationsPreserveLegacyBytesExceptEmptyList(bool explicitEmpty)
    {
        // Independent pre-change bytes for a minimal class-filtered lane, with elapsed time pinned.
        const string legacyRecord = """
            {"mode":"Enforce","decision":"plan","outcome":"Resolved","fullGateReason":null,"floorLanes":[],"guardClasses":[],"lanes":[{"name":"Balanced","disposition":"narrowed","selectedClasses":["Candidate.A"],"skippedClasses":["Candidate.B"],"filter":"(FullyQualifiedName=Candidate.A)","laneSkipReason":null,"classSkipReason":"selected-execution-class-not-selected"}],"executedClassCount":1,"skippedClassCount":1,"proposedExecutedClassCount":1,"proposedSkippedClassCount":1,"selectionMilliseconds":0,"structuralCoverageScoped":true,"applied":true}
            """;
        AcceptanceTestLane[] lanes = [new("Balanced", "FullyQualifiedName=Candidate.A|FullyQualifiedName=Candidate.B")];
        var session = new AcceptanceSelectedExecutionSession(AcceptanceSelectedExecutionMode.Enforce, "candidate", Changes,
            read: (_, _) => new(ReverseDependencyTestSelection.Resolved(["A"]), Classes, [], explicitEmpty ? [] : null));
        var observed = Observe(session, lanes);
        observed = observed with { SelectedExecution = observed.SelectedExecution! with { SelectionMilliseconds = 0 } };
        var recordJson = JsonSerializer.Serialize(observed.SelectedExecution, AcceptanceGateLanePlan.JsonOptions);
        var expectedRecord = legacyRecord.Replace("\"guardClasses\":[],", "\"guardClasses\":[],\"obligationClasses\":[],", StringComparison.Ordinal);
        Assert.Equal(expectedRecord, recordJson);
        Assert.Equal("(FullyQualifiedName=Candidate.A)", Assert.Single(session.Transform(lanes)).Filter);
        const string legacyEnvelopePrefix = """
            {"schemaVersion":"gate-lane-plan.v1","attemptId":"attempt","scheduled":["Balanced"],"skipped":[],"balancedLayout":null,"selectedExecution":
            """;
        const string legacyEnvelopeSuffix = """
            ,"testSettings":{},"laneTestSettings":[]}
            """;
        Assert.Equal(legacyEnvelopePrefix + expectedRecord + legacyEnvelopeSuffix, observed.ToJson());
    }
}
