using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-local stores and a substituted focused runner; no external gate.
public sealed class SelectedExecutionBackstopFailureRerunTests
{
    [Fact]
    public async Task AllOriginalFailuresPassCoversTheRangeWithoutMissOrSuspension()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(selection =>
        {
            calls++;
            Assert.Equal(new[] { "Candidate.X.First", "Candidate.X.Second" }, selection.Identities);
            return Executed([]);
        });
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopGateRunner.Classify(
            Red("Candidate.X.First", "Candidate.X.Second"), rerun));
        await fixture.Land();
        await fixture.Service(capacity: false);
        var queued = (await fixture.Store.ReadAsync())!.Request!;

        await fixture.Finish();

        var state = (await fixture.Store.ReadAsync())!;
        Assert.Null(state.OpenMiss);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        // Flakes that pass when run again cover the range: the request is finished, not repeated.
        Assert.Null(state.Request);
        Assert.Equal(queued.ToCommit, state.CoveredCommit);
        var green = Assert.Single(fixture.Lines.Where(line => line.Contains("result=green ")));
        Assert.Contains("rerun-passed: originalCount=2; flaky=Candidate.X.First,Candidate.X.Second", green);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=infrastructure-failure "));
        Assert.DoesNotContain(await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType),
            evt => evt.Operation == "red");
        Assert.Equal(1, calls);
        Assert.Equal(new[] { queued.Id }, fixture.Calls.Select(call => call.Id));
    }

    [Fact]
    public async Task MissNamesOnlyOriginalFailureConfirmedAgain()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var calls = 0;
        const string confirmed = "Candidate.X.Case(1)";
        var rerun = new SelectedExecutionBackstopFailureRerun(selection =>
        {
            calls++;
            Assert.Equal(new[] { "Candidate.X.Case" }, selection.Identities);
            Assert.Equal("FullyQualifiedName~Candidate.X.Case", selection.Request);
            return Executed([confirmed, "Candidate.X.Case(3)"]);
        });
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopGateRunner.Classify(
            Red(confirmed, "Candidate.X.Case(2)"), rerun));
        await fixture.Land();

        await fixture.Finish();

        var state = (await fixture.Store.ReadAsync())!;
        var miss = Assert.IsType<SelectedExecutionBackstopMissRecord>(state.OpenMiss);
        Assert.Equal(new[] { confirmed }, miss.FailingTests);
        Assert.Null(state.Request);
        Assert.Equal(miss.Id, (await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home))!.MissId);
        var item = Assert.Single(await fixture.Items.ListAsync());
        Assert.Equal(new[] { confirmed }, JsonSerializer.Deserialize<SelectedExecutionBackstopMissRecord>(item.Body)!.FailingTests);
        Assert.Contains("failingTests=1", Assert.Single(fixture.Lines.Where(line => line.Contains("result=red "))));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("infrastructure-failure")]
    public void NonRedDoesNotRerun(string result)
    {
        var rerun = new SelectedExecutionBackstopFailureRerun(_ => throw new InvalidOperationException("unexpected rerun"));
        var outcome = new SelectedExecutionBackstopOutcome(result, [], []);
        Assert.Same(outcome, rerun.Confirm(outcome));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unqualified")]
    [InlineData("later-unqualified")]
    public void UnselectableFailuresDoNotRerun(string scenario)
    {
        var called = false;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ => { called = true; return Executed([]); });
        string[] identities = scenario switch
        {
            "empty" => [],
            "unqualified" => ["unqualified"],
            _ => Enumerable.Range(0, 10).Select(index => "Candidate.X.Method" + index)
                .Append("unqualified").ToArray()
        };
        var outcome = rerun.Confirm(new("red", identities, ["lane"]));
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.StartsWith("rerun-unavailable:", outcome.InfrastructureReason);
        Assert.False(called);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void MethodLimitBoundary_RunsEveryConsecutiveBatch(int count)
    {
        var identities = Enumerable.Range(0, count).Select(index => "Candidate.X.Method" + index).ToArray();
        var calls = new List<ConductorCohortFocusedSelection>();
        var rerun = new SelectedExecutionBackstopFailureRerun(selection =>
        {
            calls.Add(selection);
            return Executed([]) with { ExecutedCount = selection.Identities.Count };
        });

        var outcome = rerun.Confirm(new("red", identities, ["lane"]));

        Assert.Equal(count == 10 ? 1 : 2, calls.Count);
        Assert.Equal(identities.Take(10).Order(StringComparer.Ordinal), calls[0].Identities);
        if (count == 11) Assert.Equal(new[] { identities[10] }, calls[1].Identities);
        Assert.Equal("green", outcome.Result);
        Assert.StartsWith("rerun-passed:", outcome.InfrastructureReason);
    }

    [Fact]
    public void TwentyEightFailures_AllBatchesPass_CoversEachDistinctMethodOnce()
    {
        var identities = ManyFailures();
        var expected = Enumerable.Range(0, 16).Select(index => "Candidate.X.Method" + index)
            .Concat(Enumerable.Range(0, 6).Select(index => "Candidate.X.Case" + index)).ToArray();
        var calls = new List<ConductorCohortFocusedSelection>();
        var rerun = new SelectedExecutionBackstopFailureRerun(selection =>
        {
            calls.Add(selection);
            return Executed([]) with { ExecutedCount = selection.Identities.Count };
        });

        var outcome = rerun.Confirm(new("red", identities, ["lane"]));

        Assert.Equal(28, identities.Length);
        Assert.Equal(12, identities.Count(identity => identity.Contains('(')));
        Assert.Equal(new[] { 10, 10, 2 }, calls.Select(call => call.Identities.Count));
        Assert.All(calls, call =>
        {
            Assert.InRange(call.Identities.Count, 1, AcceptanceFailureAttributionPlanner.MaxCandidateRerunIdentities);
            Assert.All(call.Identities, identity => Assert.DoesNotContain("(", identity));
            Assert.Equal(string.Join("; ", call.Identities.Select(identity => "FullyQualifiedName~" + identity)), call.Request);
        });
        var selected = calls.SelectMany(call => call.Identities).ToArray();
        Assert.Equal(22, selected.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Order(StringComparer.Ordinal), selected.Order(StringComparer.Ordinal));
        for (var index = 0; index < calls.Count; index++)
            Assert.Equal(expected.Skip(index * 10).Take(10).Order(StringComparer.Ordinal), calls[index].Identities);
        Assert.Equal("green", outcome.Result);
        Assert.StartsWith("rerun-passed: originalCount=28;", outcome.InfrastructureReason);
        Assert.Empty(outcome.FailingTests);
        Assert.Empty(outcome.FailedChecks);
    }

    [Fact]
    public void SecondBatchRepeatsTheoryRow_ConfirmsOnlyOriginalAndFinishesAllBatches()
    {
        const string confirmed = "Candidate.X.Case0(0)";
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(selection =>
        {
            calls++;
            if (calls != 2) return Executed([]);
            Assert.Contains("Candidate.X.Case0", selection.Identities);
            return Executed([confirmed, "Candidate.X.Case0(99)"]);
        });

        var outcome = rerun.Confirm(new("red", ManyFailures(), ["lane"]));

        Assert.Equal(3, calls);
        Assert.Equal("red", outcome.Result);
        Assert.Equal(new[] { confirmed }, outcome.FailingTests);
        Assert.Equal(new[] { "lane" }, outcome.FailedChecks);
    }

    [Theory]
    [InlineData("rejected", false)]
    [InlineData("apparatus", false)]
    [InlineData("exception", false)]
    [InlineData("apparatus", true)]
    [InlineData("exception", true)]
    public void SecondBatchInterrupted_StopsWithInfrastructure(string scenario, bool firstRed)
    {
        var identities = ManyFailures();
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            if (calls == 1) return Executed(firstRed ? [identities[0]] : []);
            return scenario switch
            {
                "rejected" => ConductorAcceptanceCohortFocusedAttribution.Unusable(ConductorCohortFocusedPassKind.SelectionRejected, "rejected"),
                "apparatus" => ConductorAcceptanceCohortFocusedAttribution.Unusable(ConductorCohortFocusedPassKind.InfrastructureFailure, "apparatus"),
                _ => throw new IOException("focused runner failed")
            };
        });

        var outcome = rerun.Confirm(new("red", identities, ["lane"]));

        Assert.Equal(2, calls);
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.StartsWith(scenario == "exception" ? "rerun-failed: IOException: focused runner failed" : "rerun-not-executed:",
            outcome.InfrastructureReason);
        Assert.EndsWith(" (batch 2)", outcome.InfrastructureReason);
        Assert.Empty(outcome.FailingTests);
        Assert.Empty(outcome.FailedChecks);
    }

    [Fact]
    public void SecondBatchCancellation_PropagatesAndStopsLaterBatches()
    {
        var calls = 0;
        var cancellation = new TaskCanceledException("cancelled");
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            if (calls == 2) throw cancellation;
            return Executed([]);
        });

        var caught = Assert.Throws<TaskCanceledException>(() => rerun.Confirm(new("red", ManyFailures(), ["lane"])));

        Assert.Same(cancellation, caught);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnexpectedFailure_RemainsInfrastructureEvenWithOriginalRepeat(bool repeat)
    {
        var identities = ManyFailures();
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            return calls == 2
                ? Executed(repeat ? [identities[0], "Candidate.X.Other"] : ["Candidate.X.Other"])
                : Executed([]);
        });

        var outcome = rerun.Confirm(new("red", identities, ["lane"]));

        Assert.Equal(3, calls);
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.StartsWith("rerun-incomplete:", outcome.InfrastructureReason);
        Assert.Empty(outcome.FailingTests);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExecutedBatchWithoutPassedChecks_NeverReportsGreen(int incompleteBatch)
    {
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            return Executed([]) with { AllChecksPassed = calls != incompleteBatch };
        });

        var outcome = rerun.Confirm(new("red", ManyFailures(), ["lane"]));

        Assert.Equal(3, calls);
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.StartsWith("rerun-incomplete:", outcome.InfrastructureReason);
    }

    [Fact]
    public void RepeatsAcrossBatches_PreserveOriginalIdentityOrder()
    {
        var identities = ManyFailures();
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            return Executed([" " + identities[calls == 1 ? 16 : calls == 2 ? 0 : 27] + " "]);
        });

        var outcome = rerun.Confirm(new("red", identities, ["lane"]));

        Assert.Equal(3, calls);
        Assert.Equal("red", outcome.Result);
        Assert.Equal(new[] { identities[0], identities[16], identities[27] }, outcome.FailingTests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("new-failure")]
    [InlineData("rejected")]
    [InlineData("apparatus")]
    [InlineData("exception")]
    public void UnconfirmedFailuresRemainInfrastructure(string scenario)
    {
        var calls = 0;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ =>
        {
            calls++;
            return scenario switch
            {
                "missing" => Executed([]) with { AllChecksPassed = false },
                "new-failure" => Executed(["Candidate.X.Other"]),
                "rejected" => ConductorAcceptanceCohortFocusedAttribution.Unusable(ConductorCohortFocusedPassKind.SelectionRejected, "rejected"),
                "apparatus" => ConductorAcceptanceCohortFocusedAttribution.Unusable(ConductorCohortFocusedPassKind.InfrastructureFailure, "apparatus"),
                _ => throw new IOException("focused runner failed")
            };
        });
        var outcome = SelectedExecutionBackstopGateRunner.Classify(Red("Candidate.X.First"), rerun);
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.Empty(outcome.FailingTests);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CancellationPropagates()
    {
        var rerun = new SelectedExecutionBackstopFailureRerun(_ => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => SelectedExecutionBackstopGateRunner.Classify(Red("Candidate.X.First"), rerun));
    }

    private static string[] ManyFailures() => Enumerable.Range(0, 16).Select(index => "Candidate.X.Method" + index)
        .Concat(Enumerable.Range(0, 12).Select(index => $"Candidate.X.Case{index % 6}({index / 6})")).ToArray();

    private static AcceptanceVerificationResult Red(params string[] identities) =>
        new(false, false, 1, "failed", Checks: [new("lane", false, 1, "failed", FailingTestIdentities: identities)]);

    private static ConductorCohortFocusedPassResult Executed(string[] failures) =>
        new(ConductorCohortFocusedPassKind.Executed, failures, failures.Length == 0 ? [] : ["focused"], 2,
            failures.Length == 0, []);
}
