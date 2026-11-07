using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each harness owns its SQLite stores and logs, with an injected clock and model.
public sealed class ConductorStewardCaseGSubmitAdmissionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Xunit.Theory]
    [Xunit.InlineData("terminal")]
    [Xunit.InlineData("version-unresolved")]
    [Xunit.InlineData("version-throws")]
    public async Task Goal_no_longer_live_at_submit_skips_once_without_intent(string scenario)
    {
        StewardCaseGHarness? active = null;
        var calls = 0;
        using var h = new StewardCaseGHarness(version: _ =>
        {
            calls++;
            if (scenario == "terminal")
                active!.Kernel.CancelGoal(active.Goal.Id, "Ended after trigger observation");
            if (calls == 1) return 7;
            return scenario == "version-throws" ? throw new IOException("version unavailable") : null;
        });
        active = h;
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));

        h.Host.ServiceTick(h.Kernel);

        Assert.Equal(scenario == "terminal" ? 1 : 2, calls);
        await AssertSkipped(h, trigger, "goal-not-live");
    }

    [Xunit.Fact]
    public async Task Out_of_boundary_at_submit_skips_once_without_intent()
    {
        StewardCaseGHarness? active = null;
        var calls = 0;
        using var h = new StewardCaseGHarness(version: _ =>
        {
            if (++calls == 1)
                active!.Kernel.RecordGoalPolicyDecision(active.Goal.Id, "New acceptance activity");
            return 7;
        });
        active = h;
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));

        h.Host.ServiceTick(h.Kernel);

        Assert.Equal(2, calls);
        await AssertSkipped(h, trigger, "steward-capability-boundary");
    }

    [Xunit.Fact]
    public async Task Consecutive_ticks_over_skipped_trigger_record_one_skip()
    {
        var calls = 0;
        using var h = new StewardCaseGHarness(version: _ => ++calls == 1 ? 7 : null);
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        h.Host.ServiceTick(h.Kernel);
        await AssertSkipped(h, trigger, "goal-not-live");
        Assert.Equal(trigger.Identity, Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow)).Identity);

        h.Host.ServiceTick(h.Kernel);

        await AssertSkipped(h, trigger, "goal-not-live");
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public async Task Skipped_trigger_is_serviced_no_action_and_never_claimed()
    {
        var calls = 0;
        using var h = new StewardCaseGHarness(version: _ => ++calls == 1 ? 7 : null);
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));

        h.Host.ServiceTick(h.Kernel);
        await AssertSkipped(h, trigger, "goal-not-live");
        Assert.False(h.Triggers.NeedsInspection(trigger.GoalId, trigger.TaskId,
            trigger.CandidateSha, trigger.Kind));
        Assert.Null(h.Triggers.ClaimReady(h.Clock.UtcNow, new HashSet<string> { h.Goal.Id.Value }));
        h.Host.ServiceTick(h.Kernel);

        await AssertSkipped(h, trigger, "goal-not-live");
        Assert.Equal(2, calls);
    }

    [Xunit.Fact]
    public async Task Admissible_submit_enqueues_intent_the_coordinator_applies()
    {
        using var h = new StewardCaseGHarness();
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));

        h.Host.ServiceTick(h.Kernel);

        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var stored = h.Triggers.Observe(trigger);
        Assert.Equal("serviced", stored.Status);
        Assert.Equal("route-submitted", stored.Outcome);
        Assert.Equal(intent.Id, stored.IntentId);
        await AssertApplied(h, intent);
    }

    [Xunit.Fact]
    public async Task Verifying_without_live_gate_trigger_is_admitted_without_boundary_rejection()
    {
        using var h = new StewardCaseGHarness();
        Assert.Equal(GoalStatus.Verifying, h.Goal.Status);
        var trigger = Assert.Single(h.CaseG.Detect(h.Goal, h.Clock.UtcNow));
        Assert.Equal(ConductorStewardTriggerKind.VerifyingWithoutLiveGate, trigger.Kind);

        h.Host.ServiceTick(h.Kernel);

        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson, Json)!;
        Assert.True(ConductorStewardCaseG.Admits(h.Goal, h.Target, payload, h.Clock.UtcNow));
        Assert.Null(ConductorStewardSubmitAdmission.Evaluate(h.Goal, h.Target, payload, h.Clock.UtcNow, _ => 7));
        await AssertApplied(h, intent);
    }

    [Xunit.Fact]
    public async Task Stale_precondition_precedes_capability_boundary_and_follows_liveness()
    {
        using var h = new StewardCaseGHarness();
        h.Host.ServiceTick(h.Kernel);
        var intent = Assert.Single(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var payload = JsonSerializer.Deserialize<AdjudicateOperatorIntentPayload>(intent.PayloadJson, Json)!;
        payload = payload with { Precondition = payload.Precondition! with { TaskStatus = "Assigned" } };
        h.Kernel.RecordGoalPolicyDecision(h.Goal.Id, "New acceptance activity");
        Assert.True(AdjudicationPrecondition.IsStale(payload, h.Goal, h.Target));
        Assert.False(ConductorStewardCaseG.Admits(h.Goal, h.Target, payload, h.Clock.UtcNow));

        Assert.Equal("stale-precondition", ConductorStewardSubmitAdmission.Evaluate(
            h.Goal, h.Target, payload, h.Clock.UtcNow, _ => 7));
        Assert.Equal("goal-not-live", ConductorStewardSubmitAdmission.Evaluate(
            h.Goal, h.Target, payload, h.Clock.UtcNow, _ => null));
    }

    private static async Task AssertSkipped(StewardCaseGHarness h, ConductorStewardTrigger trigger, string reason)
    {
        Assert.Empty(await h.Intents.ListForGoalAsync(h.Goal.Id.Value));
        var events = Events(h);
        // A claimed trigger can end as serviced/no-action too, but emits a steward event.
        Assert.DoesNotContain(events, record => record.EventKind == "steward" &&
            record.Detail.Contains($"trigger={trigger.Identity} ", StringComparison.Ordinal));
        var entry = Assert.Single(events.Where(record => record.EventKind == "steward-skip"));
        Assert.Equal(h.Goal.Id.Value, entry.GoalId);
        Assert.Contains($"reason={reason}", entry.Detail);
        Assert.Contains("case=G", entry.Detail);
        Assert.Contains($"candidate={StewardCaseGHarness.Sha}", entry.Detail);
        Assert.Contains($"trigger={trigger.Identity}", entry.Detail);
        var stored = h.Triggers.Observe(trigger);
        Assert.Equal("serviced", stored.Status);
        Assert.Equal("no-action", stored.Outcome);
        Assert.Null(stored.IntentId);
        Assert.Equal(0, h.Model.Calls);
        Assert.Null(h.Host.CurrentRound);
    }

    private static async Task AssertApplied(StewardCaseGHarness h, OperatorIntentRecord intent)
    {
        var decisions = CollaborationItemStore.ForDirectory(h.Root);
        var coordinator = new OperatorIntentCoordinator(h.Intents, utcNow: () => h.Clock.UtcNow,
            decisions: decisions, goalStateVersionResolver: _ => 7,
            evidenceResolver: new AdjudicationEvidenceResolver(h.Root), hasLiveAcceptanceAttempt: _ => false);
        var result = coordinator.ExecutePending(h.Kernel, h.Goal);
        Assert.True(result.MutatedGoalState);
        Assert.False(result.RejectedAdjudication);
        coordinator.CompletePersisted([h.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await h.Intents.GetAsync(intent.Id))!.Status);
        var decision = await decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, decision!.Effect!.Status);
        Assert.NotEqual("steward-capability-boundary", decision.Effect.Result);
        Assert.DoesNotContain(Events(h), record => record.EventKind == "steward-skip");
        Assert.Equal(0, h.Model.Calls);
        Assert.Null(h.Host.CurrentRound);
    }

    private static ConductEventRecord[] Events(StewardCaseGHarness h) => File.ReadAllLines(h.ConductPath)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!).ToArray();
}
