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
        Assert.Empty(record.UnresolvedObligationSelectors);
        Assert.False(json.RootElement.GetProperty("selectedExecution").TryGetProperty("unresolvedObligationSelectors", out _));
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("FullyQualifiedName~Candidate.B")]
    [InlineData("tests/Package.cs")]
    [InlineData("Candidate.Missing.Check")]
    [InlineData("../Candidate.B")]
    [InlineData("b")]
    [InlineData("Missing.Check")]
    public void UnresolvedSelector_NarrowsLikeSelectorRemoved(string selector)
    {
        var session = WithObligations([selector, "B"]);
        var baseline = WithObligations(["B"]);
        Assert.Equal(baseline.Transform(Lanes), session.Transform(Lanes));
        var record = session.Observe(new("gate-lane-plan.v1", "attempt", [], [])).SelectedExecution!;
        Assert.True(session.Applied);
        Assert.Equal("plan", record.Decision);
        Assert.Equal("Resolved", record.Outcome);
        Assert.Null(record.FullGateReason);
        Assert.Equal(selector, Assert.Single(record.UnresolvedObligationSelectors).Selector);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
    }

    [Fact]
    public void ClassOutsideEveryLane_RecordsDiagnostic()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["A"]),
            [new("Only A", "FullyQualifiedName=Candidate.A")], Classes, [], [], ["B"]);
        Assert.True(plan.HasPlan);
        Assert.Null(plan.FullGateReason);
        Assert.Equal("B", Assert.Single(plan.UnresolvedObligationSelectors).Selector);
        Assert.Empty(plan.ObligationClasses);
    }

    [Fact]
    public void SimpleMethodForm_RetainsWholeClass()
    {
        AcceptanceTestClassDescriptor[] classes = [new("Some.Alpha", null), new("Some.Other", null)];
        AcceptanceTestLane[] lanes = [new("Both", "FullyQualifiedName=Some.Alpha|FullyQualifiedName=Some.Other")];
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["Alpha.SomeMethod"]);

        Assert.True(plan.HasPlan);
        var lane = Assert.Single(plan.Lanes);
        Assert.Equal(new[] { "Some.Alpha" }, lane.SelectedClasses);
        Assert.Equal(new[] { "Some.Other" }, lane.SkippedClasses);
        Assert.Equal(new[] { "Some.Alpha" }, plan.ObligationClasses);
        Assert.Equal("(FullyQualifiedName=Some.Alpha)", Assert.Single(plan.ExecutionLanes).Filter);
        Assert.Equal(new AcceptanceSelectedExecutionMethodFormResolution("Alpha.SomeMethod", "Some.Alpha"),
            Assert.Single(plan.MethodFormResolutions));
    }

    [Fact]
    public void FullNameMethodForm_PreservesDirectClassResolution()
    {
        AcceptanceTestClassDescriptor[] classes = [new("Namespace.Alpha", null), new("Other.Alpha", null)];
        AcceptanceTestLane[] lanes = [new("Both", "FullyQualifiedName=Namespace.Alpha|FullyQualifiedName=Other.Alpha")];
        var method = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["Namespace.Alpha.SomeMethod"]);
        var direct = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["Namespace.Alpha"]);

        Assert.True(method.HasPlan);
        Assert.True(direct.HasPlan);
        Assert.Equal(new[] { "Namespace.Alpha" }, method.ObligationClasses);
        Assert.Equal(new[] { "Namespace.Alpha" }, Assert.Single(method.Lanes).SelectedClasses);
        Assert.Equal(new[] { "Other.Alpha" }, Assert.Single(method.Lanes).SkippedClasses);
        Assert.Equal(method.ObligationClasses, direct.ObligationClasses);
        Assert.Equal(Assert.Single(method.Lanes).SelectedClasses, Assert.Single(direct.Lanes).SelectedClasses);
        Assert.Equal(new AcceptanceSelectedExecutionMethodFormResolution("Namespace.Alpha.SomeMethod", "Namespace.Alpha"),
            Assert.Single(method.MethodFormResolutions));
        Assert.Empty(direct.MethodFormResolutions);
    }

    [Theory]
    [InlineData("Outer+Inner")]
    [InlineData("Namespace.Outer+Inner")]
    public void NestedTypeMethodForm_ResolvesFullName(string classFullName)
    {
        var selector = classFullName + ".Method";
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            [new("Nested", "FullyQualifiedName=" + classFullName)], [new(classFullName, null)], [], [], [selector]);

        Assert.True(plan.HasPlan);
        Assert.Equal(new[] { classFullName }, Assert.Single(plan.Lanes).SelectedClasses);
        Assert.Equal(new AcceptanceSelectedExecutionMethodFormResolution(selector, classFullName),
            Assert.Single(plan.MethodFormResolutions));
    }

    [Theory]
    [InlineData("Missing.Check")]
    [InlineData("B.Check")]
    [InlineData("A.Missing.Check")]
    [InlineData("A+Missing")]
    [InlineData("Candidate.A+Missing")]
    [InlineData("a.Check")]
    [InlineData("A..Check")]
    [InlineData("A.Check()")]
    [InlineData(".A.Check")]
    [InlineData("A.Check.")]
    [InlineData(" A.Check")]
    [InlineData("A.Check ")]
    [InlineData("A.Check\n")]
    [InlineData("A.1Check")]
    [InlineData("")]
    public void UnresolvedMethodForm_RecordsOriginalSelector(string selector)
    {
        AcceptanceTestLane[] lanes = [new("Only A", "FullyQualifiedName=Candidate.A")];
        var control = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, Classes, [], [], ["A.Check"]);
        Assert.True(control.HasPlan);
        Assert.Equal(new[] { "Candidate.A" }, Assert.Single(control.Lanes).SelectedClasses);

        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, Classes, [], [], [selector]);

        Assert.True(plan.HasPlan);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, plan.Outcome);
        Assert.Null(plan.FullGateReason);
        Assert.Equal(selector, Assert.Single(plan.UnresolvedObligationSelectors).Selector);
        Assert.Empty(plan.ObligationClasses);
        Assert.Empty(plan.MethodFormResolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectClassMatch_TakesPrecedenceOverMethodForm(bool directClassInLane)
    {
        AcceptanceTestClassDescriptor[] classes = [new("Alpha", null), new("Alpha.Check", null)];
        var filter = "FullyQualifiedName=Alpha" + (directClassInLane ? "|FullyQualifiedName=Alpha.Check" : "");
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            [new("Classes", filter)], classes, [], [], ["Alpha.Check"]);

        Assert.True(plan.HasPlan);
        Assert.Null(plan.FullGateReason);
        if (directClassInLane)
        {
            Assert.Equal(new[] { "Alpha.Check" }, plan.ObligationClasses);
            Assert.Empty(plan.UnresolvedObligationSelectors);
        }
        else
        {
            Assert.Equal("Alpha.Check", Assert.Single(plan.UnresolvedObligationSelectors).Selector);
            Assert.Equal("Alpha.Check", plan.UnresolvedObligationSelectors[0].ClosestCandidateClassNames[0]);
            Assert.Empty(plan.ObligationClasses);
        }
        Assert.Empty(plan.MethodFormResolutions);
    }

    [Fact]
    public void SharedSimpleNameMethodForm_RecordsAllClassesInOrdinalOrder()
    {
        AcceptanceTestClassDescriptor[] classes = [new("Two.B", null), new("One.Nested+B", null), new("One.B", null)];
        AcceptanceTestLane[] lanes = [new("Both", "FullyQualifiedName~One|FullyQualifiedName~Two")];
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            lanes, classes, [], [], ["B.Check"]);

        Assert.True(plan.HasPlan);
        Assert.Equal(new[] { "One.B", "One.Nested+B", "Two.B" }, plan.ObligationClasses);
        Assert.Equal(plan.ObligationClasses, Assert.Single(plan.Lanes).SelectedClasses);
        Assert.Equal(new[] { ("B.Check", "One.B"), ("B.Check", "One.Nested+B"), ("B.Check", "Two.B") },
            plan.MethodFormResolutions.Select(item => (item.Selector, item.ClassFullName)));
    }

    [Fact]
    public void SharedSimpleNameMethodForm_RejectsAnyClassOutsideLanes()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            [new("Only one", "FullyQualifiedName=One.B")], [new("One.B", null), new("Two.B", null)], [], [], ["B.Check"]);

        Assert.True(plan.HasPlan);
        Assert.Null(plan.FullGateReason);
        Assert.Equal("B.Check", Assert.Single(plan.UnresolvedObligationSelectors).Selector);
        Assert.Empty(plan.ObligationClasses);
        Assert.Empty(plan.MethodFormResolutions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MethodFormRecord_ListsDistinctSelectorClassPairsInLanePlanJson(bool shadow)
    {
        var mode = shadow ? AcceptanceSelectedExecutionMode.Shadow : AcceptanceSelectedExecutionMode.Enforce;
        var session = WithObligations(["B.Check", "Candidate.B.Check", "B", "B.Check", "B.OtherCheck"], mode);
        var observation = Observe(session);
        var record = observation.SelectedExecution!;
        var expected = new[] { ("B.Check", "Candidate.B"), ("Candidate.B.Check", "Candidate.B"), ("B.OtherCheck", "Candidate.B") };

        Assert.Equal("plan", record.Decision);
        Assert.Equal(mode == AcceptanceSelectedExecutionMode.Enforce, session.Applied);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
        Assert.Equal(expected, record.MethodFormResolutions.Select(item => (item.Selector, item.ClassFullName)));
        using var json = JsonDocument.Parse(observation.ToJson());
        Assert.Equal(expected, json.RootElement.GetProperty("selectedExecution").GetProperty("methodFormResolutions")
            .EnumerateArray().Select(item => (item.GetProperty("selector").GetString(), item.GetProperty("classFullName").GetString())));
        if (mode == AcceptanceSelectedExecutionMode.Shadow)
            Assert.Equal(Lanes.Select(lane => lane.Name), observation.Scheduled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedSelector_PreservesSuccessfulMethodFormResolutions(bool unresolvedFirst)
    {
        var selectors = unresolvedFirst ? new[] { "Missing.Check", "B.Check" } : new[] { "B.Check", "Missing.Check" };
        var record = Observe(WithObligations(selectors)).SelectedExecution!;

        Assert.Equal("plan", record.Decision);
        Assert.Equal("Resolved", record.Outcome);
        Assert.Null(record.FullGateReason);
        Assert.Equal("Missing.Check", Assert.Single(record.UnresolvedObligationSelectors).Selector);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
        Assert.Equal(new AcceptanceSelectedExecutionMethodFormResolution("B.Check", "Candidate.B"),
            Assert.Single(record.MethodFormResolutions));
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
        Assert.True(wrongCase.HasPlan);
        Assert.Null(wrongCase.FullGateReason);
        Assert.Equal("b", Assert.Single(wrongCase.UnresolvedObligationSelectors).Selector);
        Assert.Empty(wrongCase.ObligationClasses);
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
    public void ShadowUnresolvedSelector_RecordsDiagnosticWithoutChangingExecution()
    {
        var session = WithObligations(["Missing", "B"], AcceptanceSelectedExecutionMode.Shadow);
        var observation = Observe(session);
        Assert.False(session.Applied);
        Assert.Equal(Lanes.Select(lane => lane.Name), observation.Scheduled);
        Assert.Equal("plan", observation.SelectedExecution!.Decision);
        Assert.Equal("Resolved", observation.SelectedExecution.Outcome);
        Assert.Null(observation.SelectedExecution.FullGateReason);
        Assert.Equal("Missing", Assert.Single(observation.SelectedExecution.UnresolvedObligationSelectors).Selector);
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
        expectedRecord = expectedRecord[..^1] + ",\"methodFormResolutions\":[]}";
        Assert.Equal(expectedRecord, recordJson);
        Assert.Empty(JsonSerializer.Deserialize<AcceptanceSelectedExecutionRecord>(legacyRecord,
            AcceptanceGateLanePlan.JsonOptions)!.MethodFormResolutions);
        Assert.Empty(JsonSerializer.Deserialize<AcceptanceSelectedExecutionRecord>(legacyRecord,
            AcceptanceGateLanePlan.JsonOptions)!.UnresolvedObligationSelectors);
        Assert.Equal("(FullyQualifiedName=Candidate.A)", Assert.Single(session.Transform(lanes)).Filter);
        const string legacyEnvelopePrefix = """
            {"schemaVersion":"gate-lane-plan.v1","attemptId":"attempt","scheduled":["Balanced"],"skipped":[],"balancedLayout":null,"selectedExecution":
            """;
        const string legacyEnvelopeSuffix = """
            ,"testSettings":{},"laneTestSettings":[]}
            """;
        Assert.Equal(legacyEnvelopePrefix + expectedRecord + legacyEnvelopeSuffix, observed.ToJson());
    }

    [Theory]
    [InlineData("FullyQualifiedName~Candidate.B")]
    [InlineData("Missing")]
    [InlineData("b")]
    [InlineData("Missing.Check")]
    public void UnresolvedSelector_NeverReturnsFullGatePlan(string selector)
    {
        AcceptanceSelectedExecutionPlan Create(IReadOnlyList<string> selectors) =>
            AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved(["A"]),
                Lanes, Classes, ["Chaos gate"], ["Candidate.Guard"], selectors);
        var baseline = Create(["B"]);
        var plan = Create([selector, "B"]);

        Assert.True(plan.HasPlan);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, plan.Outcome);
        Assert.Null(plan.FullGateReason);
        Assert.Equal(selector, Assert.Single(plan.UnresolvedObligationSelectors).Selector);
        Assert.Equal(new[] { "Candidate.B" }, plan.ObligationClasses);
        Assert.Equal(JsonSerializer.Serialize(baseline.Lanes), JsonSerializer.Serialize(plan.Lanes));
        Assert.Equal(baseline.ExecutionLanes, plan.ExecutionLanes);
    }

    [Theory]
    [InlineData("unmapped", "UnmappedTestClass: Unknown", "Abandoned")]
    [InlineData("changes", "Candidate changes are unavailable.", "Unavailable")]
    [InlineData("inventory", "Candidate test-class inventory is unavailable.", "Unavailable")]
    public void OtherFullGateReasons_RemainUnchanged(string scenario, string reason, string outcome)
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(scenario == "changes" ? [] : Changes,
            ReverseDependencyTestSelection.Resolved([scenario == "unmapped" ? "Unknown" : "A"]),
            Lanes, scenario == "inventory" ? [] : Classes, [], [], ["Missing"]);

        Assert.False(plan.HasPlan);
        Assert.Equal(outcome, plan.Outcome.ToString());
        Assert.Equal(reason, plan.FullGateReason);
        Assert.Equal(Lanes, plan.ExecutionLanes);
        Assert.Empty(plan.UnresolvedObligationSelectors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedSelectors_RecordDistinctDiagnosticsInEncounterOrder(bool shadow)
    {
        const string selector = "WrapperChildFlagCollisionGuardTests";
        const string candidate = "Candidate.WrapperChildFlagCollisionTests";
        var mode = shadow ? AcceptanceSelectedExecutionMode.Shadow : AcceptanceSelectedExecutionMode.Enforce;
        var session = new AcceptanceSelectedExecutionSession(mode, "candidate", Changes, read: (_, _) =>
            new(ReverseDependencyTestSelection.Resolved(["A"]), [.. Classes, new(candidate, null)], [],
                [selector, "wrapperchildflagcollisiontests", selector, "B", "Missing.Check", "Missing.Check", "WRAPPERCHILDFLAGCOLLISIONTESTS"]));
        var observation = Observe(session, [new("Selectable", "FullyQualifiedName~Candidate.")]);
        var record = observation.SelectedExecution!;
        Assert.Equal("plan", record.Decision);
        Assert.Null(record.FullGateReason);
        Assert.Equal(!shadow, record.Applied);
        Assert.Equal(new[] { "Candidate.B" }, record.ObligationClasses);
        Assert.DoesNotContain(candidate, record.Lanes.SelectMany(lane => lane.SelectedClasses));
        Assert.Equal(new[] { selector, "wrapperchildflagcollisiontests", "Missing.Check", "WRAPPERCHILDFLAGCOLLISIONTESTS" },
            record.UnresolvedObligationSelectors.Select(item => item.Selector));
        Assert.Equal(candidate, record.UnresolvedObligationSelectors[0].ClosestCandidateClassNames[0]);
        Assert.Equal(candidate, record.UnresolvedObligationSelectors[1].ClosestCandidateClassNames[0]);
        using var json = JsonDocument.Parse(observation.ToJson());
        var diagnostics = json.RootElement.GetProperty("selectedExecution").GetProperty("unresolvedObligationSelectors");
        Assert.Equal(record.UnresolvedObligationSelectors.Select(item => item.Selector),
            diagnostics.EnumerateArray().Select(item => item.GetProperty("selector").GetString()));
        for (var index = 0; index < diagnostics.GetArrayLength(); index++)
            Assert.Equal(record.UnresolvedObligationSelectors[index].ClosestCandidateClassNames,
                diagnostics[index].GetProperty("closestCandidateClassNames").EnumerateArray().Select(item => item.GetString()));
        var restored = JsonSerializer.Deserialize<AcceptanceSelectedExecutionRecord>(
            JsonSerializer.Serialize(record, AcceptanceGateLanePlan.JsonOptions), AcceptanceGateLanePlan.JsonOptions)!;
        Assert.Equal(record.UnresolvedObligationSelectors.Select(item => item.Selector),
            restored.UnresolvedObligationSelectors.Select(item => item.Selector));
        Assert.Equal(record.UnresolvedObligationSelectors[0].ClosestCandidateClassNames,
            restored.UnresolvedObligationSelectors[0].ClosestCandidateClassNames);
    }

    [Fact]
    public void ClosestCandidates_UseNormalizedDistanceOrdinalTiesAndDistinctTopThree()
    {
        AcceptanceTestClassDescriptor[] candidates =
            [new("D.Foo", null), new("C.Abcd", null), new("B.Abcdf", null), new("A.Abcdef", null), new("A.Abcdef", null)];
        var diagnostic = AcceptanceUnresolvedObligationSelector.Create("abcde", candidates, wellFormed: true);
        Assert.Equal(new[] { "A.Abcdef", "B.Abcdf", "C.Abcd" }, diagnostic.ClosestCandidateClassNames);
        Assert.Empty(AcceptanceUnresolvedObligationSelector.Create("Missing", [], wellFormed: true).ClosestCandidateClassNames);
    }

    [Theory]
    [InlineData("WrapperChildFlagCollisionGuardTests", true)]
    [InlineData("Namespace.WrapperChildFlagCollisionGuardTests.Check", true)]
    [InlineData("Namespace.Outer+WrapperChildFlagCollisionGuardTests.Check", true)]
    [InlineData("WrapperChildFlagCollisionGuardTests()", false)]
    public void ClosestCandidates_UseClassComponentOrMalformedRawText(string selector, bool wellFormed)
    {
        var diagnostic = AcceptanceUnresolvedObligationSelector.Create(selector,
            [new("Candidate.OtherTests", null), new("Candidate.WrapperChildFlagCollisionTests", null)], wellFormed);
        Assert.Equal(selector, diagnostic.Selector);
        Assert.Equal("Candidate.WrapperChildFlagCollisionTests", diagnostic.ClosestCandidateClassNames[0]);
    }

    [Fact]
    public void MalformedSelector_RanksRawTextInsteadOfParsingClassComponent()
    {
        var plan = AcceptanceSelectedExecutionPlan.Create(Changes, ReverseDependencyTestSelection.Resolved([]),
            [new("Classes", "FullyQualifiedName~C.")], [new("C.B", null), new("C.A_B__", null)], [], [], ["A.B()"]);
        Assert.True(plan.HasPlan);
        Assert.Null(plan.FullGateReason);
        Assert.Equal("C.A_B__", Assert.Single(plan.UnresolvedObligationSelectors).ClosestCandidateClassNames[0]);
    }
}
