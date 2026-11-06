using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fixed replay clocks, immutable snapshots and private fixture artifacts.
public sealed class OwnerConsoleFinishedGateViewTests
{
    private const string Goal = "12345678";
    private const string Execution = "12345678-gate-20261106160000000-0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 16, 5, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("passed", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("aborted", "cancelled")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("canceled", "cancelled")]
    [InlineData("gate-passed", "passed")]
    public void TerminalReceipt_MemberAndExecutionShowOutcomeAndFinishAge(
        string result, string outcome)
    {
        var projection = Finished(result);

        var member = Assert.IsType<OwnerConsoleGateProgress>(projection.ForMember(Goal));
        var execution = Assert.IsType<OwnerConsoleGateProgress>(projection.ForExecution(Execution));
        Assert.False(member.Live);
        Assert.False(member.LanePlanAvailable);
        Assert.Equal(Now.AddMinutes(-2), member.FinishedAt);
        Assert.Equal(outcome, member.FinalOutcome);
        AssertFinished(member, outcome, "finished 2m ago");
        AssertFinished(execution, outcome, "finished 2m ago");
        if (result == "failed")
        {
            var lines = OwnerConsoleGateLayout.Lines(member, 180, false);
            Assert.Contains("FAILED", lines);
            Assert.Contains(lines, line => line.Contains("Console", StringComparison.Ordinal) &&
                line.Contains("failed", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EjectedMember_UsesEjectionAgeWhileSurvivorRemainsLive()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var projection = fixture.Build();

        var member = Assert.IsType<OwnerConsoleGateProgress>(
            projection.ForMember(OwnerConsoleGateExecutionReplayFixture.Ejected));
        Assert.False(member.Live);
        Assert.Equal(OwnerConsoleGateExecutionReplayFixture.Now.AddSeconds(-560), member.FinishedAt);
        Assert.True(projection.ForMember(OwnerConsoleGateExecutionReplayFixture.Survivor)!.Live);
        AssertFinished(member, "ejected / awaiting survivor", "ejected 9m ago");
    }

    [Fact]
    public void FailedGate_PreservesFailingTestNamesInCollapsedView()
    {
        var member = Finished("failed").ForMember(Goal)!;
        member = member with
        {
            Lanes = member.Lanes.Select(lane => lane with
                { FailingTestNames = ["ExampleTests.Regression"] }).ToArray()
        };

        var lines = OwnerConsoleGateLayout.Lines(member, 180, false);

        Assert.Contains("FAILED", lines);
        Assert.Contains("     ExampleTests.Regression", lines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinishedPlannedGate_HidesScheduleAndEtaRegardlessOfExpansion(bool expanded)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var events = fixture.Events.Append(new(
            OwnerConsoleGateExecutionReplayFixture.Now.AddMinutes(-2), "acceptance", null,
            $"ACCEPTANCE goal={OwnerConsoleGateExecutionReplayFixture.Survivor} result=passed " +
            $"attempt={OwnerConsoleGateExecutionReplayFixture.Current}"));
        var gate = fixture.Build(events).ForMember(OwnerConsoleGateExecutionReplayFixture.Survivor)!;

        Assert.True(gate.LanePlanAvailable);
        AssertFinished(gate, "passed", "finished 2m ago");
        var view = string.Join('\n', OwnerConsoleGateLayout.Lines(gate, 180, expanded));
        Assert.DoesNotContain("ETA", view);
        Assert.DoesNotContain("lane plan", view);
        Assert.DoesNotContain("estimate", view);
    }

    [Theory]
    [InlineData("ACCEPTANCE_COHORT_EXIT", "outcome=passed", "passed")]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "result=completed verdict=passed", "passed")]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "result=completed verdict=failed", "failed")]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "result=completed verdict=unknown", null)]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "result=completed", null)]
    [InlineData("ACCEPTANCE_COHORT_RECONCILED_DEAD", "result=none", null)]
    [InlineData("ACCEPTANCE_COHORT_RECONCILED_DEAD", "result=unreadable error=missing Status", null)]
    [InlineData("ACCEPTANCE_COHORT_ABANDON", "", "cancelled")]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "", null)]
    public void OuterCompletion_UsesRecordedOutcomeAcrossReplayPaths(
        string receipt, string result, string? outcome)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var completion = new OwnerConductEvent(OwnerConsoleGateExecutionReplayFixture.Now.AddMinutes(-2),
            "acceptance-cohort", null,
            $"{receipt} attempt={OwnerConsoleGateExecutionReplayFixture.Outer} " +
            $"members={OwnerConsoleGateExecutionReplayFixture.Survivor} {result}");
        var lateChild = OwnerConsoleGateExecutionReplayFixture.Progress(-30, "late-child",
            "verification-check", "Console");
        var earlyChild = OwnerConsoleGateExecutionReplayFixture.Progress(-180, "early-child",
            "verification-check", "Console");
        var gates = new[]
        {
            fixture.Build([earlyChild, completion]).ForExecution("early-child"),
            fixture.Build([completion,
                OwnerConsoleGateExecutionReplayFixture.OuterEvent(-60), lateChild]).ForExecution("late-child"),
            fixture.Build([completion]).ForMember(OwnerConsoleGateExecutionReplayFixture.Survivor)
        };

        foreach (var projected in gates)
        {
            var gate = Assert.IsType<OwnerConsoleGateProgress>(projected);
            Assert.False(gate.Live);
            Assert.Equal(completion.Timestamp, gate.FinishedAt);
            Assert.Equal(outcome, gate.FinalOutcome);
            AssertFinished(gate, outcome ?? "outer attempt completed", "finished 2m ago");
            if (outcome is null)
            {
                Assert.Equal("gate: outer attempt completed, finished 2m ago",
                    OwnerConsoleGateFormatter.StatusSegment(gate));
            }
        }
    }

    [Fact]
    public void UnmatchedTerminal_DoesNotCloseIdentifiedExecutionByMembership()
    {
        var projection = OwnerConsoleGateProgress.Build(Now,
        [
            new(Now.AddMinutes(-3), "gate-progress", Goal,
                $"PHASE_PROGRESS phase=verification-check execution={Execution} members={Goal} target=Console"),
            new(Now.AddMinutes(-2), "acceptance", Goal, $"ACCEPTANCE goal={Goal} result=passed")
        ], [new("Console", 10)], []);

        var gate = projection.ForExecution(Execution)!;

        Assert.True(gate.Live);
        Assert.Null(gate.FinishedAt);
        Assert.Contains("scheduled total unknown; ETA unknown (lane plan unavailable)",
            OwnerConsoleGateLayout.Lines(gate, 180, false));
    }

    [Fact]
    public void OldCompletion_LeavesMemberViewAndRetainsExecutionHistory()
    {
        var projection = Finished("passed", Now.AddMinutes(20));

        Assert.Null(projection.ForMember(Goal));
        AssertFinished(projection.ForExecution(Execution)!, "passed", "finished 22m ago");
    }

    [Fact]
    public void FutureCompletion_ClampsFinishAgeToZero()
    {
        var gate = Finished("passed", Now.AddMinutes(-3)).ForMember(Goal)!;

        AssertFinished(gate, "passed", "finished 0m ago");
    }

    [Fact]
    public void LiveGate_UnknownPlanKeepsExistingCountsAndEtaLine()
    {
        var gate = Live() with { LanePlanAvailable = false, EtaAvailable = false };

        var lines = OwnerConsoleGateLayout.Lines(gate, 180, false);

        Assert.Contains(" x 0 failed   > 0 running   + 0 passed   = 0 cached   . 0 queued  (lane plan unavailable)", lines);
        Assert.Contains("scheduled total unknown; ETA unknown (lane plan unavailable)", lines);
        Assert.Equal("gate: solo 1234 running 5m", OwnerConsoleGateFormatter.StatusSegment(gate));
    }

    [Fact]
    public void LiveGate_KnownPlanKeepsExactProgressBarAndStatus()
    {
        var gate = Live();

        var lines = OwnerConsoleGateLayout.Lines(gate, 180, false);

        Assert.Contains("[######----] estimate 62%  ~9m left", lines);
        Assert.Equal("gate: solo 1234 0/0 ~9m", OwnerConsoleGateFormatter.StatusSegment(gate));
    }

    [Fact]
    public void LiveFailingGate_KeepsFailureDetailsWithoutProgressLine()
    {
        var gate = Live() with
        {
            LanePlanAvailable = false, EtaAvailable = false,
            Lanes = [new("Console", OwnerConsoleGateLaneState.Done, "failed", "local",
                TimeSpan.FromSeconds(1), 10, false)]
        };

        var lines = OwnerConsoleGateLayout.Lines(gate, 180, false);

        Assert.Contains("GATE solo | elapsed 5m | FAILING", lines);
        Assert.Contains("FAILED", lines);
        Assert.Contains(lines, line => line.EndsWith("(lane plan unavailable)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("ETA", StringComparison.Ordinal));
        Assert.Equal("gate: solo 1234 FAILING", OwnerConsoleGateFormatter.StatusSegment(gate));
    }

    private static OwnerConsoleGateProgress Finished(string result, DateTimeOffset? now = null) =>
        OwnerConsoleGateProgress.Build(now ?? Now,
        [
            new(Now.AddMinutes(-3), "gate-progress", Goal,
                $"PHASE_PROGRESS phase=shard-complete execution={Execution} members={Goal} " +
                $"target=Console outcome={(result == "failed" ? "failed" : "passed")} elapsed_ms=1000"),
            new(Now.AddMinutes(-2), "acceptance", Goal,
                $"ACCEPTANCE goal={Goal} result={result} attempt={Execution}")
        ], [new("Console", 10)], []);

    private static OwnerConsoleGateProgress Live() => new(true, "solo", [new(Goal, "Goal")],
        Now.AddMinutes(-5), TimeSpan.FromMinutes(5), [], .62, 540, 0, 0,
        LanePlanAvailable: true, EtaAvailable: true);

    private static void AssertFinished(OwnerConsoleGateProgress gate, string outcome, string age)
    {
        foreach (var compact in new[] { false, true })
        {
            var segment = OwnerConsoleGateFormatter.StatusSegment(gate, compact);
            Assert.Contains(outcome, segment);
            Assert.Contains(age, segment);
        }
        var view = string.Join('\n', OwnerConsoleGateLayout.Lines(gate, 180, false));
        Assert.Contains(outcome, view);
        Assert.Contains(age, view);
        Assert.DoesNotContain("lane plan", view);
        Assert.DoesNotContain("ETA", view);
        Assert.DoesNotContain("schedule unknown", view);
    }
}
