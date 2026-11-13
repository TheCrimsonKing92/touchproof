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
    [InlineData("over-limit")]
    public void UnselectableFailuresDoNotRerun(string scenario)
    {
        var called = false;
        var rerun = new SelectedExecutionBackstopFailureRerun(_ => { called = true; return Executed([]); });
        string[] identities = scenario switch
        {
            "empty" => [],
            "unqualified" => ["unqualified"],
            _ => Enumerable.Range(0, 11).Select(index => "Candidate.X.Method" + index).ToArray()
        };
        var outcome = rerun.Confirm(new("red", identities, ["lane"]));
        Assert.Equal("infrastructure-failure", outcome.Result);
        Assert.StartsWith("rerun-unavailable:", outcome.InfrastructureReason);
        Assert.False(called);
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

    private static AcceptanceVerificationResult Red(params string[] identities) =>
        new(false, false, 1, "failed", Checks: [new("lane", false, 1, "failed", FailingTestIdentities: identities)]);

    private static ConductorCohortFocusedPassResult Executed(string[] failures) =>
        new(ConductorCohortFocusedPassKind.Executed, failures, failures.Length == 0 ? [] : ["focused"], 2,
            failures.Length == 0, []);
}
