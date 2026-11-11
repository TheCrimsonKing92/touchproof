using Mcg.AgentOrchestrator.Core;

public sealed class ProviderTaskAvailabilityEvidenceTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("Success", false)]
    [InlineData("AVAILABLE", false)]
    [InlineData("The prompt says AVAILABLE", false)]
    [InlineData("Validated task response", true)]
    public void TaskAvailability_RequiresVerifiedCompletion(string output, bool verified)
    {
        var clock = new Clock();
        var kernel = new AgentOrchestratorKernel(clock);
        var agent = new AgentDefinition(new("tester"), "Tester", AgentRole.Tester,
            new("xAI", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey));
        Goal NewGoal()
        {
            var goal = kernel.CreateGoal("Availability evidence", [new TaskSpec(TaskId.New(), "Verify fixture", AgentRole.Tester)]);
            kernel.ActivateGoal(goal.Id, [agent]);
            return goal;
        }
        var source = NewGoal();
        kernel.RecordTaskDispatch(source.Id, source.Tasks.Single().Id,
            new("grok-cli", "fixture", @"C:\fixture", clock.UtcNow, ProviderName: "xAI"));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        kernel.RecordTaskVerification(source.Id, source.Tasks.Single().Id,
            new("fixture", @"C:\fixture", 1, "", "Grok Build usage balance exhausted", clock.UtcNow,
                ProviderFailureKind: ProviderFailureKind.BudgetExhausted));
        Assert.True(ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(kernel.ProviderHoldScope, "xai::<provider-default>", out var hold));
        var probe = NewGoal();
        clock.UtcNow = clock.UtcNow.AddMinutes(30);
        kernel.RecordTaskNote(probe.Id, probe.Tasks.Single().Id, ProviderBudgetHoldSchedules.FormatProbe(
            new(hold.BindingKey, clock.UtcNow, 1, "backoff") { HoldPlacedAt = hold.PlacedAt }));
        var started = clock.UtcNow;
        kernel.RecordTaskDispatch(probe.Id, probe.Tasks.Single().Id,
            new("grok-cli", "fixture", @"C:\fixture", started, ProviderName: "xAI"));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        kernel.RecordTaskVerification(probe.Id, probe.Tasks.Single().Id,
            new("fixture", @"C:\fixture", 0, output, "", clock.UtcNow, DispatchStartedAt: started,
                CompletionVerdictVerifiedSuccess: verified));
        Assert.Equal(!verified, ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(kernel.ProviderHoldScope, hold.BindingKey, out var remaining));
        if (verified) return;
        Assert.Equal(hold, remaining);
        var schedule = ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, remaining, clock.UtcNow);
        Assert.Equal(2, schedule.Attempt);
        Assert.Equal("inconclusive", schedule.LastProbeOutcome);
        Assert.Equal(clock.UtcNow.AddMinutes(60), schedule.NextProbeAt);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-11-10T12:00:00Z");
    }
}
