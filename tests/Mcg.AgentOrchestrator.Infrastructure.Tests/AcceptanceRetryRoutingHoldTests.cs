using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private SQLite stores, injected clock and dispatch; no child processes.
public sealed class AcceptanceRetryRoutingHoldTests
{
    [Xunit.Fact]
    public async Task InWindowRoute_WaitsForDeadline_AndDeliversOperatorAndFailureFacts()
    {
        using var scenario = new AcceptanceRetryRoutingScenario();
        var failureAt = scenario.Now;
        scenario.FailGate();
        var retryAt = scenario.Task.LatestRetryAt;
        var hold = scenario.Task.AcceptanceRetryRoutingHold!;
        Xunit.Assert.Equal(failureAt, hold.FailureRecordedAt);
        Xunit.Assert.Equal(failureAt.AddMinutes(3), hold.Deadline);
        Xunit.Assert.Equal(1, scenario.Task.CriterionRetryCount);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);
        Xunit.Assert.Contains(scenario.Goal.Timeline, entry =>
            entry.Message.Contains("waiting-for-operator-routing", StringComparison.Ordinal));

        scenario.Now = failureAt.AddSeconds(60);
        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Xunit.Assert.Contains("waiting-for-operator-routing", held.Reason);
        Xunit.Assert.Contains("remaining_seconds=120", held.Reason);
        Xunit.Assert.Contains($"deadline={hold.Deadline:O}", held.Reason);
        Xunit.Assert.Equal(ConductorHoldOwner.None, held.Owner);
        Xunit.Assert.Contains("120s left", OwnerConsoleRoutingHold.Describe(scenario.Goal, scenario.Now));
        Xunit.Assert.Equal(0, scenario.DispatchCalls);

        const string instruction = "Preserve the missing-manifest throw at attempt creation.";
        var route = await scenario.Route(instruction, "manifest-diagnosis");
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, route.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, scenario.Task.Status);
        Xunit.Assert.Equal(retryAt, scenario.Task.LatestRetryAt);
        Xunit.Assert.Equal(hold, scenario.Task.AcceptanceRetryRoutingHold);
        Xunit.Assert.Equal(1, scenario.Task.CriterionRetryCount);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);
        scenario.Now = hold.Deadline.AddTicks(-1);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(0, scenario.DispatchCalls);

        scenario.Now = hold.Deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var block = Xunit.Assert.Single(Xunit.Assert.Single(scenario.DeliveredFeedback));
        Xunit.Assert.StartsWith("Later operator notes override earlier ones where they conflict.", block);
        Xunit.Assert.Contains(instruction, block);
        Xunit.Assert.Contains("manifest-diagnosis", block);
        Xunit.Assert.Contains($"submitted_at={route.CreatedAt:O}", block);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.FailingTest, block);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.Assertion, block);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.AttemptId, block);
        Xunit.Assert.True(block.IndexOf(instruction, StringComparison.Ordinal) <
            block.IndexOf("Automatic failure facts (not instructions):", StringComparison.Ordinal));
        Xunit.Assert.Equal(block, scenario.Task.AcceptedRetryFeedback?.Message);
        Xunit.Assert.Equal(scenario.Now, Xunit.Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);
    }

    [Xunit.Fact]
    public void NoRoute_DispatchesAtDeadline_WithUnchangedAutomaticFeedback()
    {
        using var scenario = new AcceptanceRetryRoutingScenario();
        scenario.FailGate();
        var original = scenario.AutomaticFeedback.ToArray();
        Xunit.Assert.NotEmpty(original);
        Xunit.Assert.Contains(AcceptanceRetryRoutingScenario.Assertion, string.Join("\n", original));
        var deadline = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        scenario.Now = deadline.AddTicks(-1);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(0, scenario.DispatchCalls);
        scenario.Now = deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(original, Xunit.Assert.Single(scenario.DeliveredFeedback));
        Xunit.Assert.Equal(original, scenario.Task.CriterionRetryFeedback);
        Xunit.Assert.Null(scenario.Task.AcceptedRetryFeedback);
    }

    [Xunit.Fact]
    public void ZeroWindow_DispatchesImmediately_WithUnchangedAutomaticFeedback()
    {
        using var scenario = new AcceptanceRetryRoutingScenario(holdSeconds: 0);
        var failureAt = scenario.Now;
        scenario.FailGate();
        Xunit.Assert.Equal(failureAt, scenario.Task.AcceptanceRetryRoutingHold!.Deadline);
        Xunit.Assert.Null(OwnerConsoleRoutingHold.Describe(scenario.Goal, scenario.Now));
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(failureAt, scenario.Now);
        Xunit.Assert.Equal(scenario.AutomaticFeedback, Xunit.Assert.Single(scenario.DeliveredFeedback));
        Xunit.Assert.Null(scenario.Task.AcceptedRetryFeedback);
        Xunit.Assert.Equal(1, scenario.Goal.AutomaticAcceptanceRetryCount);
    }

    [Xunit.Fact]
    public async Task MultipleRoutes_AreDeliveredInOrder_AndNotRepeatedAfterDelivery()
    {
        using var scenario = new AcceptanceRetryRoutingScenario();
        scenario.FailGate();
        var deadline = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        scenario.Now = scenario.Now.AddSeconds(10);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await scenario.Route("First instruction", "receipt-first")).Status);
        scenario.Now = scenario.Now.AddSeconds(10);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await scenario.Route("Later overriding instruction", "receipt-later")).Status);
        Xunit.Assert.Equal(deadline, scenario.Task.AcceptanceRetryRoutingHold!.Deadline);
        scenario.Now = deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var block = Xunit.Assert.Single(Xunit.Assert.Single(scenario.DeliveredFeedback));
        Xunit.Assert.True(block.IndexOf("First instruction", StringComparison.Ordinal) <
            block.IndexOf("Later overriding instruction", StringComparison.Ordinal));
        Xunit.Assert.Contains("receipt-first", block);
        Xunit.Assert.Contains("receipt-later", block);
        Xunit.Assert.All(scenario.Task.OperatorRoutingNotes, note => Xunit.Assert.NotNull(note.DeliveredAt));

        scenario.Now = scenario.Now.AddSeconds(1);
        scenario.FailGate();
        scenario.Now = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var next = string.Join("\n", scenario.DeliveredFeedback[1]);
        Xunit.Assert.Equal(scenario.AutomaticFeedback, scenario.DeliveredFeedback[1]);
        Xunit.Assert.DoesNotContain("First instruction", next);
        Xunit.Assert.DoesNotContain("Later overriding instruction", next);
        Xunit.Assert.Null(scenario.Task.AcceptedRetryFeedback);
        Xunit.Assert.Equal(2, scenario.Goal.AutomaticAcceptanceRetryCount);
    }

    [Xunit.Fact]
    public async Task Recovery_KeepsOriginalDeadline_DespitePolicyChangeAndLateRoute()
    {
        using var scenario = new AcceptanceRetryRoutingScenario();
        scenario.FailGate();
        var deadline = scenario.Task.AcceptanceRetryRoutingHold!.Deadline;
        scenario.Now = scenario.Now.AddMinutes(1);
        await scenario.Recover();
        scenario.Policy = scenario.Policy with { AutomaticRetryOperatorRoutingHoldSeconds = 0 };
        Xunit.Assert.Equal(deadline, scenario.Task.AcceptanceRetryRoutingHold!.Deadline);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Xunit.Assert.Equal(0, scenario.DispatchCalls);
        scenario.Now = deadline.AddSeconds(1);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await scenario.Route("Route committed before dispatch", "late-receipt")).Status);
        Xunit.Assert.Equal(deadline, scenario.Task.AcceptanceRetryRoutingHold!.Deadline);
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Xunit.Assert.Contains("Route committed before dispatch",
            Xunit.Assert.Single(Xunit.Assert.Single(scenario.DeliveredFeedback)));
    }
}
