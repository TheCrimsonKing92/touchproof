using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: directly constructed events and supplied lookups; no external state.
public sealed class OwnerAcceptanceNarrationTests
{
    [Fact]
    public void TryNarrate_SinglePassed_StatesOnlyChecksPassed()
    {
        OwnerConductEvent[] events =
            [new(DateTimeOffset.UnixEpoch, "acceptance", "11111111", "ACCEPTANCE result=passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Passed acceptance must not request failure evidence."), null, out var result));

        Assert.Equal("Search: passed its tests", result.Sentence);
        Assert.Equal("The required checks passed.", result.Why);
        Assert.Equal("The required checks passed.", result.Next);
        Assert.Equal("The required checks passed.", result.Act);
        foreach (var text in new[] { result.Sentence, result.Why, result.Next, result.Act })
            Assert.DoesNotContain("land", text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.GoalPrefixes);
        Assert.Equal(new OwnerConsoleTitleSpan(0, 6, 0), Assert.Single(result.Spans.Titles));
        Assert.Equal(8, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_OwnNewTestFailed_ReturnsWorkToDeveloper()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance", "11111111",
            "ACCEPTANCE result=failed tests=NewTests.Behavior")];
        var evidenceCalls = 0;

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, source =>
        {
            Assert.Same(events[0], source);
            evidenceCalls++;
            return new(["NewTests.Behavior"], [], true);
        }, null, out var result));

        Assert.Equal("Search: failed its tests (its own new test NewTests.Behavior failed); sent back to the Developer", result.Sentence);
        Assert.Equal("its own new test NewTests.Behavior failed.", result.Why);
        Assert.Equal("sent back to the Developer.", result.Next);
        Assert.Equal("No. The conductor is handling the next step.", result.Act);
        Assert.Equal(1, evidenceCalls);
        Assert.Null(result.GoalPrefixes);
        Assert.Equal(8, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_JointPassed_PreservesMemberOrderAndTitleSpans()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Passed joint run must not request failure evidence."), null, out var result));

        Assert.Equal("Joint test run for Search, Export: passed", result.Sentence);
        Assert.Equal("The joint checks passed.", result.Why);
        Assert.Equal("The required checks passed.", result.Next);
        Assert.Equal("No. You can let the conductor continue.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
        Assert.Equal(new[] { new OwnerConsoleTitleSpan(19, 6, 0), new OwnerConsoleTitleSpan(27, 6, 0) }, result.Spans.Titles);
        Assert.Equal(35, result.Spans.OutcomeStart);
        Assert.Equal(6, result.Spans.OutcomeLength);
    }

    [Fact]
    public void TryNarrate_InteractionOnlyFailure_DoesNotBlamePassingMembers()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=interaction-only attribution=InteractionOnly partitions=11111111:Passed,22222222:Passed")];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, null, null, out var result));

        Assert.Equal("Joint test run for Search, Export: failed (the changes failed when tested together); awaiting the conductor's next step", result.Sentence);
        Assert.Equal("the changes failed when tested together.", result.Why);
        Assert.Equal("awaiting the conductor's next step.", result.Next);
        Assert.Equal("No. The conductor is handling the next step.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
    }

    [Fact]
    public void TryNarrate_SupersededCohortChild_OnlyNarratesLaterOutcome()
    {
        OwnerConductEvent[] events =
        [
            new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
                "ACCEPTANCE_COHORT_CHILD_COMPLETED members=11111111+22222222 verdict=failed"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), "acceptance-cohort", null,
                "ACCEPTANCE_COHORT members=22222222,11111111 outcome=passed")
        ];

        Assert.False(OwnerAcceptanceNarration.TryNarrate(events, 0, Name,
            _ => throw new InvalidOperationException("Superseded child must not request failure evidence."), null, out _));
        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 1, Name, null, null, out var result));

        Assert.Equal("Joint test run for Export, Search: passed", result.Sentence);
        Assert.Equal("The joint checks passed.", result.Why);
        Assert.Equal("The required checks passed.", result.Next);
        Assert.Equal("No. You can let the conductor continue.", result.Act);
        Assert.Equal(new[] { "22222222", "11111111" }, result.GoalPrefixes);
    }

    [Fact]
    public void TryNarrate_UnresolvedMemberQuestion_RequestsOwnerAnswer()
    {
        OwnerConductEvent[] events = [new(DateTimeOffset.UnixEpoch, "acceptance-cohort", null,
            "ACCEPTANCE_COHORT members=11111111,22222222 outcome=failed attribution=SecondMemberFailed tests=Export.NewBehavior")];
        OwnerAttentionObservation[] attention = [new(new("q1", "22222222-full", OwnerQuestionKind.StewardHold,
            "Keep the export format?"), DateTimeOffset.UnixEpoch)];

        Assert.True(OwnerAcceptanceNarration.TryNarrate(events, 0, Name, null, attention, out var result));

        Assert.Equal("Joint test run for Search, Export: failed (its own new test Export.NewBehavior failed); needs you", result.Sentence);
        Assert.Equal("its own new test Export.NewBehavior failed.", result.Why);
        Assert.Equal("needs you.", result.Next);
        Assert.Equal("Yes. Review the failed check and answer the question in DECISIONS.", result.Act);
        Assert.Equal(new[] { "11111111", "22222222" }, result.GoalPrefixes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Narrate_LandingThenDelayedPass_KeepsSeparateNeutralPassItem(bool jointLanding)
    {
        var time = DateTimeOffset.UnixEpoch;
        List<OwnerConductEvent> landings = [new(time, "loop-relaunch", "11111111-goal",
            "LOOP_RELAUNCH_SCHEDULED tick=1 goal=11111111-goal")];
        if (jointLanding)
            landings.Add(new(time.AddSeconds(1), "loop-relaunch", "22222222-goal",
                "LOOP_RELAUNCH_SCHEDULED tick=1 goal=22222222-goal"));
        var baseline = Assert.Single(OwnerActivityNarrator.Narrate(landings, Title));
        Assert.Equal(jointLanding ? "Landed together: Search (11111111), Export (22222222)" :
            "Landed: Search (11111111)", baseline.Phrase);
        var passed = new OwnerConductEvent(time.AddSeconds(2), "acceptance", "11111111",
            "ACCEPTANCE goal=11111111 result=passed");

        var items = OwnerActivityNarrator.Narrate([.. landings, passed], Title);

        Assert.Equal(2, items.Count);
        var pass = items[0];
        Assert.Equal(passed.Timestamp, pass.Timestamp);
        Assert.Equal("Search: passed its tests", pass.Phrase);
        Assert.Equal("The required checks passed.", pass.Why);
        Assert.Equal("The required checks passed.", pass.Next);
        Assert.Equal("The required checks passed.", pass.Act);
        Assert.Same(passed, Assert.Single(pass.Evidence!));
        Assert.Null(pass.GoalPrefixes);
        foreach (var text in new[] { pass.Phrase, pass.Why, pass.Next, pass.Act })
            Assert.DoesNotContain("land", text, StringComparison.OrdinalIgnoreCase);
        var landed = items[1];
        Assert.Equal(baseline.Phrase, landed.Phrase);
        Assert.Equal(time, landed.Timestamp);
        Assert.Equal(baseline.Why, landed.Why);
        Assert.Equal(baseline.Next, landed.Next);
        Assert.Equal(baseline.Act, landed.Act);
        Assert.Equal(baseline.GoalPrefixes, landed.GoalPrefixes);
        Assert.Equal(baseline.Evidence, landed.Evidence);
        Assert.Equal(baseline.Spans!.Titles, landed.Spans!.Titles);

        static string Title(string? id) => Name(OwnerEventFields.Prefix(id));
    }

    [Fact]
    public void Narrate_OrdinaryPreLandingPass_StatesOnlyChecksPassed()
    {
        var time = DateTimeOffset.UnixEpoch;
        var passed = new OwnerConductEvent(time, "acceptance", "11111111",
            "ACCEPTANCE goal=11111111 result=passed");
        var landing = new OwnerConductEvent(time.AddSeconds(1), "loop-relaunch", "11111111-goal",
            "LOOP_RELAUNCH_SCHEDULED tick=1 goal=11111111-goal");

        var items = OwnerActivityNarrator.Narrate([passed, landing], Name);

        Assert.Equal(2, items.Count);
        Assert.Equal("Landed: Search (11111111)", items[0].Phrase);
        Assert.Equal(landing.Timestamp, items[0].Timestamp);
        var pass = items[1];
        Assert.Equal(time, pass.Timestamp);
        Assert.Equal("Search: passed its tests", pass.Phrase);
        Assert.Equal("The required checks passed.", pass.Why);
        Assert.Equal("The required checks passed.", pass.Next);
        Assert.Equal("The required checks passed.", pass.Act);
        Assert.Same(passed, Assert.Single(pass.Evidence!));
        foreach (var text in new[] { pass.Phrase, pass.Why, pass.Next, pass.Act })
            Assert.DoesNotContain("land", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Narrate_PassWithOperatorEvidencePending_StatesOnlyChecksPassed()
    {
        // No landing event stands in for pending Operator evidence; it does not prove that production state.
        var passed = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "11111111",
            "ACCEPTANCE goal=11111111 result=passed");

        var pass = Assert.Single(OwnerActivityNarrator.Narrate([passed], Name));

        Assert.Equal(passed.Timestamp, pass.Timestamp);
        Assert.Equal("Search: passed its tests", pass.Phrase);
        Assert.Equal("The required checks passed.", pass.Why);
        Assert.Equal("The required checks passed.", pass.Next);
        Assert.Equal("The required checks passed.", pass.Act);
        Assert.Same(passed, Assert.Single(pass.Evidence!));
        foreach (var text in new[] { pass.Phrase, pass.Why, pass.Next, pass.Act })
            Assert.DoesNotContain("land", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Narrate_FailedAcceptance_KeepsPreChangeNarration()
    {
        var failed = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "acceptance", "11111111",
            "ACCEPTANCE goal=11111111 result=failed checks=Build_main");

        var failure = Assert.Single(OwnerActivityNarrator.Narrate([failed], Name));

        Assert.Equal("Search: failed its tests (the check Build main failed); awaiting the conductor's next step",
            failure.Phrase);
        Assert.Equal("the check Build main failed.", failure.Why);
        Assert.Equal("awaiting the conductor's next step.", failure.Next);
        Assert.Equal("No. The conductor is handling the next step.", failure.Act);
        Assert.Equal(failed.Timestamp, failure.Timestamp);
        Assert.Same(failed, Assert.Single(failure.Evidence!));
        Assert.Equal(8, failure.Spans!.OutcomeStart);
        Assert.Equal(6, failure.Spans.OutcomeLength);
        Assert.Null(failure.GoalPrefixes);
    }

    private static string Name(string? id) => id == "22222222" ? "Export" : "Search";
}
