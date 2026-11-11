using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class ProgressEventPayloadSnapshotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ResetIssueTime_RoundTripsWithLegacyApplicationOrderFallback(bool legacy)
    {
        // Parallel-safe: private kernel, fake clock and in-memory JSON only.
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Persist reset authority.");
        const string binding = "xai::<provider-default>";
        var appliedAt = clock.UtcNow;
        var newer = new ProgressEvent(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote,
            ProviderBudgetHoldSchedules.FormatReset(new(binding, appliedAt.AddDays(1), "newer")), appliedAt,
            OperatorIntentCreatedAt: appliedAt.AddMinutes(-1));
        var older = newer with
        {
            Message = ProviderBudgetHoldSchedules.FormatReset(new(binding, appliedAt, "older")),
            OccurredAt = appliedAt.AddMinutes(1),
            OperatorIntentCreatedAt = appliedAt.AddMinutes(-2)
        };
        goal.Append(newer);
        goal.Append(older);
        var json = JsonSerializer.SerializeToNode(goal.ToSnapshot())!;
        if (legacy)
            foreach (var evt in json["Timeline"]!.AsArray())
                evt!.AsObject().Remove("OperatorIntentCreatedAt");

        var restored = Goal.FromSnapshot(json.Deserialize<GoalSnapshot>()!);
        var resetEvents = restored.Timeline.Where(evt =>
            ProviderBudgetHoldSchedules.TryParseReset(evt, out _)).ToArray();
        Assert.Equal(2, resetEvents.Length);
        Assert.Equal(legacy ? newer with { OperatorIntentCreatedAt = null } : newer, resetEvents[0]);
        Assert.Equal(legacy ? older with { OperatorIntentCreatedAt = null } : older, resetEvents[1]);
        var authority = Assert.IsType<ProviderBudgetHoldSchedules.ResetGeneration>(
            ProviderBudgetHoldSchedules.LatestReset([restored], binding));
        Assert.Equal(legacy ? "older" : "newer", authority.Marker.IntentId);
        Assert.Equal(legacy ? appliedAt.AddMinutes(1) : appliedAt, authority.AppliedAt);
        Assert.Equal(legacy ? (DateTimeOffset?)null : newer.OperatorIntentCreatedAt, authority.Marker.IssuedAt);
    }

    [Xunit.Fact]
    public void ToSnapshot_OperatorIntentAppliedPayload_RoundTrips()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Persist operator intent application.",
            [new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer)]);
        const string intentId = "intent-123";
        goal.Append(new ProgressEvent(
            goal.Id,
            null,
            ProgressKind.GoalPolicyDecision,
            "Display text without an application marker.",
            clock.UtcNow,
            OperatorIntentApplied: new OperatorIntentAppliedPayload(
                intentId,
                "retry",
                null,
                "operator",
                "cli",
                "local-process")));

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var restoredEvent = Assert.Single(
            restored.Timeline,
            item => item.OperatorIntentApplied?.IntentId == intentId);
        Assert.Equal(intentId, restoredEvent.OperatorIntentApplied!.IntentId);
    }

    [Xunit.Fact]
    public void ToSnapshot_HumanInputSupersededPayload_RoundTrips()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(
            "Persist supersede resolution evidence.",
            [new TaskSpec(TaskId.New(), "Implement.", AgentRole.Developer)]);
        goal.Append(new ProgressEvent(
            goal.Id,
            null,
            ProgressKind.HumanInputSuperseded,
            "Display text with resolvedStableIds=other.",
            clock.UtcNow,
            HumanInputSuperseded: new HumanInputSupersededPayload(["finding-b", "finding-a"])));

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var restoredEvent = Assert.Single(
            restored.Timeline,
            item => item.HumanInputSuperseded is not null);
        Assert.Equal(
            ["finding-b", "finding-a"],
            Assert.IsType<HumanInputSupersededPayload>(restoredEvent.HumanInputSuperseded).ResolvedStableIds);
    }

    [Xunit.Fact]
    public void ToSnapshot_ConductorTickOutcomePayload_RoundTrips()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var goal = kernel.CreateGoal("Persist typed acceptance escalation.");
        var payload = new ConductorTickOutcomePayload("Escalated", "Verified", "AcceptanceVerificationFailed");
        kernel.RecordGoalPolicyDecision(goal.Id, "Reason wording can change.", payload);

        var restored = Goal.FromSnapshot(goal.ToSnapshot());

        var evt = Assert.Single(restored.Timeline, item => item.TickOutcome is not null);
        Assert.Equal(payload, evt.TickOutcome);
        Assert.Equal("Reason wording can change.", evt.Message);
    }
}
