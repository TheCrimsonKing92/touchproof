using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its temporary host and injected clock.
public sealed class ExecutorSlotRecordingChangeTests
{
    [Theory]
    [InlineData("replacement")]
    [InlineData("correction")]
    public void SampleChange_SameCount_RecordsBeforeInterval(string change)
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var controller = fixture.Controller();
        controller.Evaluate();
        var original = fixture.Latest;
        var samples = fixture.Samples.ToArray();
        samples[0] = change == "replacement" ? samples[0] with { AttemptId = "replacement" } :
            samples[0] with { RemoteSeconds = 11 };
        fixture.Samples = samples;
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Contains("recorded=change", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, File.ReadAllLines(fixture.LedgerPath).Length);
        Assert.Equal(2, fixture.DecisionEvents.Length);
        Assert.NotEqual(original.EvaluationId, fixture.Latest.EvaluationId);
        Assert.Equal(original.Decision.TotalSamples, fixture.Latest.Decision.TotalSamples);
    }

    [Fact]
    public void SampleReordering_SameMembership_DoesNotEvaluateOrRecordEarly()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var controller = fixture.Controller();
        controller.Evaluate();
        fixture.Samples = fixture.Samples.Reverse().ToArray();
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Empty(controller.Evaluate());
        fixture.Now = fixture.Now.AddSeconds(50);
        Assert.Contains("recorded=no", Assert.Single(controller.Evaluate()));
        Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        Assert.Single(fixture.DecisionEvents);
    }

    [Fact]
    public void ExcludedArrival_TriggersEvaluationWithoutRecordingDifferentSamples()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var controller = fixture.Controller();
        controller.Evaluate();
        fixture.Samples = fixture.Samples.Concat(ExecutorSlotTestData.Jobs(count: 1,
            prefix: "expired", end: fixture.Now.AddDays(-2))).ToArray();
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Contains("recorded=no", Assert.Single(controller.Evaluate()));
        Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        Assert.Single(fixture.DecisionEvents);
    }

    [Fact]
    public void Heartbeat_BetweenDueEvaluations_WaitsForFreshDecision()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var controller = fixture.Controller();
        controller.Evaluate();
        fixture.Now = fixture.Now.AddHours(1).AddSeconds(-10);
        Assert.Contains("recorded=no", Assert.Single(controller.Evaluate()));
        fixture.Now = fixture.Now.AddSeconds(10);
        Assert.Empty(controller.Evaluate());
        Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        fixture.Now = fixture.Now.AddSeconds(50);
        Assert.Contains("recorded=heartbeat", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, File.ReadAllLines(fixture.LedgerPath).Length);
        Assert.Equal(2, fixture.DecisionEvents.Length);
        Assert.Equal(fixture.Now, fixture.Latest.ObservedAt);
    }

    [Fact]
    public void BusyOutcome_ChangesToPending_RecordsAndAppliesOnDueEvaluation()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        var controller = fixture.Controller();
        using (var claim = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, "exec", 0))
        {
            Assert.NotNull(claim);
            controller.Evaluate();
            Assert.Equal("deferred-busy", fixture.Latest.ApplicationStatus);
            fixture.Now = fixture.Now.AddMinutes(1);
            Assert.Contains("recorded=no", Assert.Single(controller.Evaluate()));
            Assert.Single(File.ReadAllLines(fixture.LedgerPath));
        }
        fixture.Now = fixture.Now.AddMinutes(1);
        Assert.Contains("application=applied", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, fixture.Slots);
        Assert.Equal("pending-handler", fixture.Latest.ApplicationStatus);
        Assert.Equal(2, File.ReadAllLines(fixture.LedgerPath).Length);
        Assert.Equal(2, fixture.DecisionEvents.Length);
    }

    [Fact]
    public void LedgerFailure_AfterRecordedEvaluation_RetriesOnNextTick()
    {
        using var fixture = new ExecutorSlotControllerFixture();
        fixture.Settings = fixture.Settings with { StableWindows = 3 };
        var fail = false;
        var controller = fixture.Controller((path, row) =>
        {
            if (fail) throw new IOException("injected-ledger-failure");
            ExecutorSlotDecisionLedger.Append(path, row);
        });
        controller.Evaluate();
        var original = fixture.Latest;
        fixture.Samples = ExecutorSlotTestData.Jobs(count: 5);
        fixture.Now = fixture.Now.AddSeconds(10);
        fail = true;
        Assert.Contains("result=audit-failed", Assert.Single(controller.Evaluate()));
        Assert.Equal(original.EvaluationId, fixture.Latest.EvaluationId);
        fail = false;
        Assert.Contains("recorded=change", Assert.Single(controller.Evaluate()));
        Assert.Equal(2, File.ReadAllLines(fixture.LedgerPath).Length);
        Assert.Equal(3, fixture.DecisionEvents.Length); // Independent event attempt on the failed append.
        Assert.Equal(5, fixture.Latest.Decision.TotalSamples);
    }
}
