using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

public sealed class ExecutorScreeningRefusalTests
{
    [Fact]
    public async Task DisabledAdmissionAndEmptyWholeGatePlanHaveDistinctIdleLines()
    {
        using var fixture = new Fixture();
        var plan = new ExecutorWholeGateTrialPlan([], [], AcceptanceDeclaredTestSettings.Empty);
        var disabledEntry = fixture.Entry with { AdmissionEnabled = false };
        var disabled = await ExecutorScreeningRunner.RunAsync(fixture.Candidate, disabledEntry, new([], []),
            fixture.Configuration, fixture.Executor, fixture.Root, fixture.ReceiptPath, "disabled",
            TimeSpan.FromMinutes(1), fixture.Clock, TimeSpan.Zero);
        var disabledWithEmptyPlan = await RunTrial(disabledEntry);
        var noLanes = await RunTrial(fixture.Entry);

        Assert.Equal("SCREEN idle reason=admission-disabled", disabled.Refusal?.FormatIdleLine());
        Assert.Equal("SCREEN idle reason=admission-disabled", disabledWithEmptyPlan.Refusal?.FormatIdleLine());
        Assert.Equal("SCREEN idle reason=no-lanes", noLanes.Refusal?.FormatIdleLine());
        Assert.Empty(fixture.Executor.Requests);
        Assert.False(File.Exists(fixture.ReceiptPath));

        Task<ExecutorScreeningOutcome<ExecutorWholeGateTrialReceipt>> RunTrial(RemoteLaneExecutorEntry entry) =>
            ExecutorScreeningRunner.RunWholeGateAsync(fixture.Candidate, entry, plan, fixture.Configuration,
                fixture.Executor, fixture.Root, fixture.ReceiptPath, "empty-plan", fixture.Clock, TimeSpan.Zero, null, null);
    }
}
