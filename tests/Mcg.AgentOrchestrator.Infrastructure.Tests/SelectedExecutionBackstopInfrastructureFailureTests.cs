using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: substituted runner and local stores only.
public sealed class SelectedExecutionBackstopInfrastructureFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InfrastructureFailureKeepsOneRequestAndDoesNotOpenMiss(bool throws)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        fixture.Run = _ => throws ? throw new IOException("fixture-runner-fault") :
            Task.FromResult(SelectedExecutionBackstopOutcome.Infrastructure("fixture-apparatus-fault"));
        await fixture.Land();
        await fixture.Finish();
        var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
        Assert.Null(state.OpenMiss);
        Assert.Null(state.CoveredCommit);
        Assert.Single(state.Uncovered);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Null(await AcceptanceSelectedExecutionSessionSuspension.ReadAsync("unused", fixture.Home));
        Assert.Contains(fixture.Lines, line => line.Contains("result=infrastructure-failure"));
        var pending = Assert.IsType<SelectedExecutionBackstopRequest>(state.Request);
        fixture.Run = _ => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
        await fixture.Finish();
        Assert.Equal(new[] { pending.Id, pending.Id }, fixture.Calls.Select(call => call.Id));
        Assert.Single((await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType)).Where(evt => evt.Operation == "queued"));
    }

    [Fact]
    public void GateClassifierKeepsApparatusSeparateFromCompletedRedAndNonTestFailures()
    {
        var infrastructure = new AcceptanceCheckResult("lane", false, 1, "apparatus", FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [infrastructure])).Result);
        var red = infrastructure with { FailureClassification = null, FailingTestIdentities = ["Tests.Broken"] };
        var miss = SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [red]));
        Assert.Equal("red", miss.Result);
        Assert.Equal(new[] { "Tests.Broken" }, miss.FailingTests);
        Assert.Equal("red", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [red with { FailingTestIdentities = null }])).Result);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, true, null, "not run")).Result);
    }

    [Theory]
    [InlineData(AcceptanceFailureClassifications.BuildPhaseTimeout)]
    [InlineData(AcceptanceFailureClassifications.MissingTool)]
    [InlineData(AcceptanceFailureClassifications.SeededRepositoryApparatus)]
    public void EveryExistingApparatusClassificationRemainsInfrastructure(string classification)
    {
        var failed = new AcceptanceCheckResult("check", false, 1, classification, FailureClassification: classification);
        Assert.Equal("infrastructure-failure", SelectedExecutionBackstopGateRunner.Classify(new(false, false, 1, "", Checks: [failed])).Result);
    }
}
