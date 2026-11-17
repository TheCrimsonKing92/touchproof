using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its repository, worktrees and injected provider probes.
public sealed class SubscriptionCooldownSubstitutionTests
{
    [Xunit.Theory]
    [Xunit.InlineData("You've hit your Fable limit · resets 11am (America/Chicago)", true)]
    [Xunit.InlineData("You've hit your Fable limit · resets 11am (America/Chicago)", false)]
    [Xunit.InlineData("You've hit your Fable limit · resets someday", true)]
    [Xunit.InlineData("ERROR: You've hit your usage limit.", false)]
    public void Start_DatelessOrUnparsedLimit_SubstitutesAndDispatchesAlternate(string refusal, bool sameProvider)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        scenario.Clock.UtcNow = DateTimeOffset.Parse("2030-11-14T12:00:00Z");
        var alternate = sameProvider
            ? Scenario.Agent("opus-planner", "Anthropic", "claude-cli", "claude-opus-5-5")
            : scenario.Alternate;
        scenario.Agents = [scenario.Primary, alternate];
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        var recordedAt = scenario.Clock.UtcNow;
        scenario.Limit(goal, refusal, model: "claude-fable-5-2", stdout: true);
        var reset = refusal.Contains("11am", StringComparison.Ordinal)
            ? DateTimeOffset.Parse("2030-11-14T17:00:00Z") : recordedAt.AddMinutes(5);
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        Assert.Equal(1, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        Assert.Equal(AgentStatus.Available, alternate.Status);
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(alternate.Id, result.Agent.Id);
        Assert.Equal(reset, result.SubscriptionCooldown!.RetryAfter);

        scenario.Resolver().ApplyBatch(goal, [task.Id]);

        Assert.Equal(alternate.Id, task.AssignedAgentId);
        Assert.Null(task.SubscriptionRetryAfter);
        var preflight = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.True(preflight.Allowed, string.Join("; ", preflight.Findings));
        Assert.DoesNotContain(preflight.Findings, finding => finding.Contains("require operator review", StringComparison.Ordinal));
        Assert.Equal(1, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        var evt = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Contains($"substitute={alternate.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"reset={reset:O}", evt.Message, StringComparison.Ordinal);
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var launches = 0;

        var started = scenario.Start(goal, requested => { launches++; return worker.Start(requested); });

        Assert.Equal(1, launches);
        Assert.Single(started.Dispatches);
        Assert.Single(started.Processes.Tasks);
        Assert.Equal(alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
    }

    [Xunit.Theory]
    [Xunit.InlineData("You've hit your Fable limit · resets 11am (America/Chicago)")]
    [Xunit.InlineData("ERROR: You've hit your usage limit.")]
    public void Start_DeferredLimitWithoutAlternate_DoesNotLaunchOriginalSeat(string refusal)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        scenario.Clock.UtcNow = DateTimeOffset.Parse("2030-11-14T12:00:00Z");
        scenario.Agents = [scenario.Primary];
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        scenario.Limit(goal, refusal, model: "claude-fable-5-2", stdout: true);
        var retryAfter = task.SubscriptionRetryAfter;
        var preflight = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.False(preflight.Allowed);
        Assert.Contains(preflight.Findings, finding => finding.StartsWith("blocked: subscription retry deferred until", StringComparison.Ordinal));
        Assert.DoesNotContain(preflight.Findings, finding => finding.Contains("require operator review", StringComparison.Ordinal));
        var launches = 0;

        var started = scenario.Start(goal, _ =>
        {
            launches++;
            throw new InvalidOperationException("Deferred seat must not launch.");
        });

        Assert.Equal(0, launches);
        Assert.Empty(started.Dispatches);
        Assert.Empty(started.Processes.Tasks);
        Assert.Single(task.DispatchHistory);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(retryAfter, task.SubscriptionRetryAfter);
        Assert.Equal(1, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
    }

    [Xunit.Fact]
    public void Apply_NoAlternate_SecondUnparsedRefusalRetainsReviewBlock()
    {
        using var scenario = new Scenario();
        scenario.Agents = [scenario.Primary];
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        const string refusal = "ERROR: You've hit your usage limit.";
        scenario.Limit(goal, refusal);
        Assert.Equal(1, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        Assert.False(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        Assert.DoesNotContain(scenario.Preflight(goal, task, scenario.Kernel.Goals).Findings,
            finding => finding.Contains("require operator review", StringComparison.Ordinal));
        scenario.Clock.UtcNow = task.SubscriptionRetryAfter!.Value.AddSeconds(1);
        var afterExpiry = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.True(afterExpiry.Allowed, string.Join("; ", afterExpiry.Findings));

        scenario.Limit(goal, refusal);
        scenario.Resolver().ApplyBatch(goal, [task.Id]);

        Assert.Equal(2, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        Assert.True(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        var preflight = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.False(preflight.Allowed);
        Assert.Contains("blocked: repeated recoverable subscription limits require operator review", preflight.Findings);
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Start_ModelLimit_SelectsFirstUsableSeat(bool bothModelsLimited)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        scenario.Clock.UtcNow = DateTimeOffset.Parse("2030-11-14T12:00:00Z");
        var seatB = Scenario.Agent("opus-planner", "Anthropic", "claude-cli", "claude-opus-5-5");
        scenario.Agents = [scenario.Primary, seatB, scenario.Alternate];
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        scenario.Limit(goal, Scenario.FableLimit, model: "claude-fable-5-2", stdout: true);
        var reset = DateTimeOffset.Parse("2030-11-18T17:00:00Z");
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        TaskSpec? sibling = null;
        if (bothModelsLimited)
        {
            sibling = scenario.Kernel.AddTask(goal.Id, AgentRole.Planner, "Another model request.", [seatB]);
            scenario.Limit(goal, Scenario.FableLimit.Replace("Fable", "Opus"), task: sibling,
                model: "claude-opus-5-5", stdout: true);
        }
        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var launches = 0;
        var started = scenario.Start(goal, requested => { launches++; return worker.Start(requested); },
            sibling is null ? null : new HashSet<TaskId> { sibling.Id });
        var expected = bothModelsLimited ? scenario.Alternate : seatB;
        Assert.Equal(1, launches);
        Assert.Single(started.Dispatches);
        Assert.Single(started.Processes.Tasks);
        Assert.Equal(expected.Id, task.AssignedAgentId);
        Assert.Equal(expected.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal(expected.Model.ModelName, task.LastDispatch.ModelName);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(ProviderLimitSignals.IsSubscriptionRetryDeferred(task, scenario.Clock.UtcNow, out _));
        var evt = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.ProviderHoldSubstituted);
        Assert.Contains($"held agent={scenario.Primary.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains($"substitute={expected.Id.Value}", evt.Message, StringComparison.Ordinal);
        Assert.Contains(scenario.Receipt(goal), evt.Message, StringComparison.Ordinal);
        Assert.Contains($"reset={reset:O}", evt.Message, StringComparison.Ordinal);
        Assert.Contains(Scenario.FableLimit, evt.Message, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, item => item.TaskId == task.Id &&
            item.Message.StartsWith("Released subscription deferral", StringComparison.Ordinal));
        if (bothModelsLimited)
            Assert.Contains(goal.Timeline, item => item.TaskId == task.Id &&
                item.Message.Contains("passed over", StringComparison.Ordinal) &&
                item.Message.Contains(seatB.Id.Value, StringComparison.Ordinal) &&
                item.Message.Contains("provider Anthropic is cooling down", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => scenario.Kernel.SubstituteProviderHeldTaskAgent(
            goal.Id, task.Id, scenario.Primary, "anthropic::<provider-default>", scenario.Receipt(goal)));
    }

    [Xunit.Fact]
    public void Plan_ModelLimit_CanAttemptSameProviderSubstitution()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var seatB = Scenario.Agent("opus-planner", "Anthropic", "claude-cli", "claude-opus-5-5");
        scenario.Agents = [scenario.Primary, seatB];
        var goal = scenario.NewGoal();
        scenario.Limit(goal, Scenario.FableLimit, model: "claude-fable-5-2", stdout: true);
        var plan = SubscriptionPlanBuilder.Build(goal, scenario.Agents, scenario.Profiles,
            now: scenario.Clock.UtcNow, sandboxOptions: scenario.Sandbox,
            commandExists: scenario.CommandExists, claudeAuthProbe: scenario.ProbeAuth);
        Assert.True(Assert.Single(plan.Items).CanAttemptSubscriptionSubstitution);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Start_RoutedModelLimit_PreservesAssignedTaskCooldown(bool complexModel)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        const string dispatchedModel = "claude-haiku-4-5";
        var primary = complexModel
            ? scenario.Primary with
            {
                Subscription = scenario.Primary.Subscription! with { ModelAlias = null },
                ComplexModel = scenario.Primary.Model with { ModelName = dispatchedModel }
            }
            : scenario.Primary with { IsProviderRoutingConstrained = null };
        scenario.Agents = [primary, scenario.Alternate];
        var goal = scenario.NewGoal(complexModel
            ? "Design and implement a comprehensive distributed production architecture with concurrent thread-safe worker persistence and integration."
            : "Plan the scoped change.");
        var task = Assert.Single(goal.Tasks);
        var initial = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.True(initial.Allowed, string.Join("; ", initial.Findings));
        Assert.Contains($"model: Anthropic/{dispatchedModel}", initial.Findings);
        scenario.Limit(goal, Scenario.FableLimit.Replace("Fable", "Haiku"), model: dispatchedModel, stdout: true);
        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out var cooldown));
        Assert.Equal("haiku", cooldown.ModelName);
        Assert.False(cooldown.AppliesToModel(ProviderLimitSignals.GetSubscriptionModelName(primary)));
        Assert.True(ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, task, primary,
            scenario.Clock.UtcNow, out var assignedCooldown));
        Assert.Equal(cooldown, assignedCooldown);
        // An unrelated seat's lookup still uses its own model rather than the held task's dispatch.
        Assert.False(ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, task,
            scenario.Primary with { Id = new("other-fable") }, scenario.Clock.UtcNow, out _));
        Assert.True(ProviderHoldAgentSubstitution.CanAttemptSubscriptionSubstitution(
            goal, task, scenario.Agents, scenario.Clock.UtcNow));
        var plan = SubscriptionPlanBuilder.Build(goal, scenario.Agents, scenario.Profiles,
            now: scenario.Clock.UtcNow, sandboxOptions: scenario.Sandbox,
            commandExists: scenario.CommandExists, claudeAuthProbe: scenario.ProbeAuth);
        Assert.True(Assert.Single(plan.Items).CanAttemptSubscriptionSubstitution);

        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var launches = 0;
        var started = scenario.Start(goal, requested => { launches++; return worker.Start(requested); });
        Assert.Equal(1, launches);
        Assert.Single(started.Dispatches);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.Contains("limited model=haiku", Assert.Single(goal.Timeline,
            item => item.Kind == ProgressKind.ProviderHoldSubstituted).Message, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Start_InheritedRoutedModelLimit_SelectsAvailableProvider(bool complexModel)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        const string dispatchedModel = "claude-haiku-4-5";
        var primary = complexModel
            ? scenario.Primary with
            {
                Subscription = scenario.Primary.Subscription! with { ModelAlias = null },
                ComplexModel = scenario.Primary.Model with { ModelName = dispatchedModel }
            }
            : scenario.Primary with { IsProviderRoutingConstrained = null };
        scenario.Agents = [primary, scenario.Alternate];
        var goal = scenario.NewGoal(complexModel
            ? "Design and implement a comprehensive distributed production architecture with concurrent thread-safe worker persistence and integration."
            : "Plan the scoped change.");
        var task = Assert.Single(goal.Tasks);
        var initial = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.True(initial.Allowed, string.Join("; ", initial.Findings));
        Assert.Contains($"model: Anthropic/{dispatchedModel}", initial.Findings);
        var sibling = scenario.Kernel.AddTask(goal.Id, AgentRole.Planner, "Another model request.", [primary]);
        scenario.Limit(goal, Scenario.FableLimit.Replace("Fable", "Haiku"), task: sibling,
            model: dispatchedModel, stdout: true);
        Assert.False(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out _));
        Assert.Null(task.LastDispatch);
        var blocked = scenario.Preflight(goal, task, scenario.Kernel.Goals);
        Assert.False(blocked.Allowed);
        Assert.Contains(blocked.Findings, finding => finding.Contains("provider Anthropic is cooling down", StringComparison.Ordinal));
        Assert.True(ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, task, primary,
            scenario.Clock.UtcNow, out var cooldown));
        Assert.Equal(sibling.Id, cooldown.SourceTaskId);
        Assert.Equal("haiku", cooldown.ModelName);
        Assert.False(cooldown.AppliesToModel(ProviderLimitSignals.GetSubscriptionModelName(primary)));
        // Looking up an unrelated seat still filters by that seat's configured model.
        Assert.False(ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, task,
            scenario.Primary with { Id = new("other-fable") }, scenario.Clock.UtcNow, out _));
        Assert.True(ProviderHoldAgentSubstitution.CanAttemptSubscriptionSubstitution(
            goal, task, scenario.Agents, scenario.Clock.UtcNow));
        var plan = SubscriptionPlanBuilder.Build(goal, scenario.Agents, scenario.Profiles,
            now: scenario.Clock.UtcNow, sandboxOptions: scenario.Sandbox,
            commandExists: scenario.CommandExists, claudeAuthProbe: scenario.ProbeAuth);
        Assert.True(Assert.Single(plan.Items, item => item.TaskId == task.Id.Value).CanAttemptSubscriptionSubstitution);

        using var worker = new ProviderHoldFallbackScenarioTests.PreparationWorker();
        var launches = 0;
        var started = scenario.Start(goal, requested => { launches++; return worker.Start(requested); },
            new HashSet<TaskId> { sibling.Id });
        Assert.Equal(1, launches);
        Assert.Single(started.Dispatches);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(scenario.Alternate.Id.Value, task.LastDispatch!.AssignedAgentId);
        Assert.Equal(scenario.Alternate.Model.ModelName, task.LastDispatch.ModelName);
        Assert.Contains(scenario.Preflights, check => check.AgentId == primary.Id && !check.Allowed);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.NotNull(sibling.SubscriptionRetryAfter);
        Assert.Contains("limited model=haiku", Assert.Single(goal.Timeline,
            item => item.TaskId == task.Id && item.Kind == ProgressKind.ProviderHoldSubstituted).Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void Apply_UnnamedLimit_SkipsSameProviderModel()
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var seatB = Scenario.Agent("opus-planner", "Anthropic", "claude-cli", "claude-opus-5-5");
        scenario.Agents = [scenario.Primary, seatB, scenario.Alternate];
        var goal = scenario.NewGoal();
        scenario.Limit(goal, model: "claude-fable-5-2");
        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(
            goal.Tasks.Single(), scenario.Clock.UtcNow, out var cooldown));
        Assert.Null(cooldown.ModelName);
        Assert.True(ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, goal.Tasks.Single(), seatB,
            scenario.Clock.UtcNow, out _));
        scenario.Resolver().ApplyBatch(goal, [goal.Tasks.Single().Id]);
        Assert.Equal(scenario.Alternate.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.Contains(scenario.Preflights, check => check.AgentId == seatB.Id && !check.Allowed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Fable", "claude-fable-5-2", "fable", true)]
    [Xunit.InlineData("Fable", "claude-FABLE-6-1", "fable", true)]
    [Xunit.InlineData("Fable", "claude-opus-5-5", "fable", false)]
    [Xunit.InlineData("Fable Claude", "claude-fable-6-1", "fable claude", true)]
    [Xunit.InlineData("Fable Claude", "claude-opus-5-5", "fable claude", false)]
    [Xunit.InlineData("Unrecognized", "claude-opus-5-5", null, true)]
    [Xunit.InlineData("Fable Unknown", "claude-opus-5-5", null, true)]
    public void Cooldown_FamilyWords_MatchModelSegments(string words, string candidateModel,
        string? expectedScope, bool blocked)
    {
        using var scenario = new Scenario("claude-fable-5-2");
        var goal = scenario.NewGoal();
        scenario.Limit(goal, Scenario.FableLimit.Replace("Fable", words), model: "claude-fable-5-2", stdout: true);
        var task = goal.Tasks.Single();
        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out var cooldown));
        Assert.Equal(expectedScope, cooldown.ModelName);
        var candidate = Scenario.Agent("candidate", "Anthropic", "claude-cli", candidateModel);
        Assert.Equal(blocked, ProviderHoldAgentSubstitution.TryGetSubscriptionCooldown(goal, task, candidate,
            scenario.Clock.UtcNow, out _));
        Assert.Equal(blocked, ProviderLimitSignals.TryGetProviderSubscriptionCooldown(goal, TaskId.New(),
            "Anthropic", candidateModel, scenario.Clock.UtcNow, out _));
        Assert.False(ProviderLimitSignals.TryGetProviderSubscriptionCooldown(goal, TaskId.New(),
            "OpenAI", candidateModel, scenario.Clock.UtcNow, out _));
        if (expectedScope is null)
        {
            scenario.Resolver().ApplyBatch(goal, [task.Id]);
            Assert.Contains("matched no segment", Assert.Single(goal.Timeline,
                item => item.Kind == ProgressKind.ProviderHoldSubstituted).Message, StringComparison.Ordinal);
        }
    }

    [Xunit.Fact]
    public void Apply_RejectedCandidates_RecordsFindingsOncePerHoldAcrossReload()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var rejected = scenario.Alternate with { Id = new("missing-launcher"),
            Subscription = scenario.Alternate.Subscription! with { WorkerProfileName = "missing-profile" } };
        var sameProvider = scenario.Primary with { Id = new("same-provider") };
        scenario.Agents = [scenario.Primary, rejected, sameProvider];
        var rejectedFindings = scenario.Preflight(goal,
            AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot(), scenario.Clock)
                .ReassignTaskAgent(goal.Id, task.Id, rejected), scenario.Kernel.Goals).Findings;
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        var note = Assert.Single(goal.Timeline, item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskNote && item.Message.Contains("rejected candidates", StringComparison.Ordinal));
        Assert.Contains(rejected.Id.Value, note.Message, StringComparison.Ordinal);
        Assert.Contains(sameProvider.Id.Value, note.Message, StringComparison.Ordinal);
        Assert.Contains("subscription retry deferred", note.Message, StringComparison.Ordinal);
        foreach (var finding in rejectedFindings.Where(value => value.StartsWith("blocked:", StringComparison.Ordinal)))
            Assert.Contains(finding, note.Message, StringComparison.Ordinal);
        scenario.Kernel = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot(), scenario.Clock);
        goal = scenario.Kernel.GetGoal(goal.Id);
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Single(goal.Timeline, item => item.Message == note.Message);
        Assert.Single(goal.Timeline, item => item.Message.Contains("rejected candidates", StringComparison.Ordinal));
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);
        scenario.Limit(goal);
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(2, goal.Timeline.Count(item => item.Message.Contains("rejected candidates", StringComparison.Ordinal)));
        scenario.Kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id,
            "Acknowledge repeated limits to isolate candidate diagnostic recording.");
        scenario.Agents = [..scenario.Agents, scenario.Alternate];
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        var passed = Assert.Single(goal.Timeline, item => item.Message.Contains("passed over", StringComparison.Ordinal));
        Assert.Contains(rejected.Id.Value, passed.Message, StringComparison.Ordinal);
        Assert.Contains(sameProvider.Id.Value, passed.Message, StringComparison.Ordinal);
        foreach (var finding in rejectedFindings.Where(value => value.StartsWith("blocked:", StringComparison.Ordinal)))
            Assert.Contains(finding, passed.Message, StringComparison.Ordinal);
        Assert.Contains("subscription retry deferred", passed.Message, StringComparison.Ordinal);
        Assert.Equal(scenario.Alternate.Id, scenario.Kernel.GetTask(goal.Id, task.Id).AssignedAgentId);
    }

    [Xunit.Fact]
    public void Resolve_ActiveClassifiedLimit_SelectsAlternateWithoutMutation()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var before = scenario.Kernel.ExportGoalSnapshot(goal.Id);
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal("anthropic::<provider-default>", result.HeldBindingKey);
        Assert.Equal(scenario.Receipt(goal), result.EvidenceReceipt);
        Assert.NotNull(result.SubscriptionCooldown);
        Assert.Equal(task.SubscriptionRetryAfter, result.SubscriptionCooldown.RetryAfter);
        Assert.Equal(before.Timeline, scenario.Kernel.ExportGoalSnapshot(goal.Id).Timeline);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.True(scenario.AuthProbes > 0);
        Assert.Contains(scenario.Preflights, check => check.AgentId == scenario.Alternate.Id && check.Allowed);
        Assert.Null(scenario.Preflight(goal, task, scenario.Kernel.Goals).ErrorCode);
    }

    [Xunit.Fact]
    public void Resolve_OperatorPin_PreservesBindingAndDeferral()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
        var reset = task.SubscriptionRetryAfter;
        Assert.IsType<ProviderHoldAgentSubstitution.Result.WaitOnPin>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.Equal(reset, task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Resolve_UnsuitableAlternates_PreservesHold()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        scenario.Agents = [scenario.Primary,
            scenario.Alternate with { Id = new("offline"), Status = AgentStatus.Offline },
            scenario.Alternate with { Id = new("wrong-role"), Role = AgentRole.Developer },
            scenario.Alternate with { Id = new("paid"), ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            scenario.Primary with { Id = new("same-provider") }];
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.NotNull(goal.Tasks.Single().SubscriptionRetryAfter);
    }

    [Xunit.Fact]
    public void Resolve_RejectedTrial_CannotReleaseNextTrialsDeferral()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var rejected = scenario.Alternate with
        {
            Id = new("missing-launcher"),
            Subscription = scenario.Alternate.Subscription! with { WorkerProfileName = "missing-profile" }
        };
        scenario.Agents = [scenario.Primary, rejected, scenario.Primary with { Id = new("same-provider") }];
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Agents = [..scenario.Agents, scenario.Alternate];
        var result = Assert.IsType<ProviderHoldAgentSubstitution.Result.Substitute>(scenario.Resolve(goal));
        Assert.Equal(scenario.Alternate.Id, result.Agent.Id);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
    }

    [Xunit.Theory]
    [Xunit.InlineData("mismatched-attempt")]
    [Xunit.InlineData("not-after-completion")]
    [Xunit.InlineData("non-limit")]
    [Xunit.InlineData("succeeded")]
    public void Cooldown_UnparsedFallback_RequiresLatestMatchingRecoverableRefusal(string control)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        scenario.Limit(goal, "ERROR: You've hit your usage limit.");
        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out _));
        var verification = Assert.Single(task.VerificationHistory);
        var fallback = task.SubscriptionRetryAfter;
        task.RecordVerification(control switch
        {
            "not-after-completion" => verification with { CompletedAt = fallback!.Value },
            "mismatched-attempt" => verification with { DispatchAttemptAt = verification.DispatchStartedAt!.Value.AddSeconds(1) },
            "non-limit" => verification with { StandardError = "ERROR: Unable to connect to API: network connection failed." },
            "succeeded" => verification with { ExitCode = 0 },
            _ => throw new InvalidOperationException("Unknown cooldown control.")
        });
        task.SetSubscriptionRetryAfter(fallback);

        Assert.True(ProviderLimitSignals.IsSubscriptionRetryDeferred(task, scenario.Clock.UtcNow, out _));
        Assert.False(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out _));
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
    }

    [Xunit.Fact]
    public void Cooldown_UnparsedFallback_DoesNotBecomeProviderWideHoldForSibling()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        var task = Assert.Single(goal.Tasks);
        scenario.Limit(goal, "ERROR: You've hit your usage limit.");
        Assert.True(ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, scenario.Clock.UtcNow, out _));
        var sibling = scenario.Kernel.AddTask(goal.Id, AgentRole.Planner, "Another request.", [scenario.Primary]);

        Assert.False(ProviderLimitSignals.TryGetProviderSubscriptionCooldown(
            goal, sibling.Id, "Anthropic", scenario.Clock.UtcNow, out _));
    }

    [Xunit.Theory]
    [Xunit.InlineData("expired")]
    [Xunit.InlineData("at-reset")]
    [Xunit.InlineData("connectivity")]
    [Xunit.InlineData("unrelated-provider")]
    public void Resolve_NonApplicableEvidence_DoesNotSubstitute(string control)
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal, control == "connectivity" ? "ERROR: Unable to connect to API: network connection failed."
            : Scenario.LimitText, control == "unrelated-provider" ? "OpenAI" : null);
        if (control is "expired" or "at-reset")
            scenario.Clock.UtcNow = goal.Tasks.Single().SubscriptionRetryAfter!.Value.AddSeconds(control == "expired" ? 1 : 0);
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [goal.Tasks.Single().Id]);
        Assert.Equal(scenario.Primary.Id, goal.Tasks.Single().AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_ChangedPinDuringPreflight_DoesNotMutateAssignment()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var alternateChecked = false;
        var resolver = new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, (g, t, scope) =>
        {
            var check = scenario.Preflight(g, t, scope);
            if (t.AssignedAgentId == scenario.Alternate.Id)
            {
                alternateChecked = true;
                scenario.Kernel.ReassignTaskAgent(goal.Id, task.Id, scenario.Primary);
            }
            return check;
        }, () => scenario.Clock.UtcNow);
        resolver.ApplyBatch(goal, [task.Id]);
        Assert.True(alternateChecked);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.NotNull(task.SubscriptionRetryAfter);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_CooldownExpiresDuringPreflight_PreservesAssignment()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        var checkedAlternate = false;
        var resolver = new ProviderHoldAgentSubstitution(scenario.Kernel, scenario.Agents, (g, t, scope) =>
        {
            var check = scenario.Preflight(g, t, scope);
            if (t.AssignedAgentId == scenario.Alternate.Id)
            {
                checkedAlternate = true;
                scenario.Clock.UtcNow = task.SubscriptionRetryAfter!.Value;
            }
            return check;
        }, () => scenario.Clock.UtcNow);
        resolver.ApplyBatch(goal, [task.Id]);
        Assert.True(checkedAlternate);
        Assert.Equal(scenario.Primary.Id, task.AssignedAgentId);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    [Xunit.Fact]
    public void Apply_AlternateHitsLimit_SharedBoundRejectsThirdAgent()
    {
        using var scenario = new Scenario();
        var goal = scenario.NewGoal();
        scenario.Limit(goal);
        var task = goal.Tasks.Single();
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        scenario.Clock.UtcNow = scenario.Clock.UtcNow.AddMinutes(1);
        scenario.Limit(goal, provider: "OpenAI");
        Assert.True(ProviderLimitSignals.RequiresSubscriptionLimitReview(task));
        scenario.Kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id,
            "Acknowledge repeated limits to isolate the automatic substitution bound.");
        var third = Scenario.Agent("third", "Ollama", "codex-oss-cli", "qwen3-coder");
        scenario.Agents = [..scenario.Agents, third];
        var scratch = AgentOrchestratorKernel.FromSnapshot(scenario.Kernel.ExportSnapshot(), scenario.Clock);
        var thirdTask = scratch.ReassignTaskAgent(goal.Id, task.Id, third);
        var check = scenario.Preflight(scratch.GetGoal(goal.Id), thirdTask, scratch.Goals);
        Assert.True(check.Allowed, string.Join("\n", check.Findings));
        Assert.IsType<ProviderHoldAgentSubstitution.Result.None>(scenario.Resolve(goal));
        scenario.Resolver().ApplyBatch(goal, [task.Id]);
        Assert.Equal(scenario.Alternate.Id, task.AssignedAgentId);
        Assert.Single(goal.Timeline, evt => evt.Kind == ProgressKind.ProviderHoldSubstituted);
    }

    internal sealed class Scenario : WorkerDispatchTestSupport, IDisposable
    {
        internal const string LimitText = "ERROR: You've hit your usage limit. Try again in 2 hours.";
        internal const string FableLimit = "You've hit your Fable limit · resets Nov 18, 11am (America/Chicago). Switch to another model, or manage usage credits to continue.";
        internal readonly string Root = CreateSeededDispatchRepository();
        internal readonly CooldownClock Clock = new();
        internal AgentOrchestratorKernel Kernel;
        internal readonly AgentDefinition Primary;
        internal readonly AgentDefinition Alternate = Agent("codex-planner", "OpenAI", "codex-cli", "gpt-6.1-sol");
        internal IReadOnlyList<AgentDefinition> Agents;
        internal readonly WorkerProfileCatalog Profiles = WorkerProfileCatalog.Default();
        internal readonly WorkerSandboxOptions Sandbox = new(true,
            WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
        internal readonly OrchestratorWorkspace Workspace;
        internal int AuthProbes;
        internal readonly List<(AgentId? AgentId, bool Allowed)> Preflights = [];

        internal Scenario(string primaryModel = "claude-opus-4.1")
        {
            Primary = Agent("claude-planner", "Anthropic", "claude-cli", primaryModel);
            Kernel = new(Clock);
            Agents = [Primary, Alternate];
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
        }

        internal Goal NewGoal(string taskDescription = "Plan the scoped change.")
        {
            var goal = InfrastructureTestSupport.MarkGoalRefined(Kernel, Kernel.CreateGoal("Plan a scoped change.",
                [new TaskSpec(TaskId.New(), taskDescription, AgentRole.Planner)]));
            Kernel.ActivateGoal(goal.Id, Agents);
            GoalWorktrees.Ensure(Root, goal.Id);
            return goal;
        }

        internal string Receipt(Goal goal) => Path.Combine(Root, goal.Id.Value + ".stderr");

        internal void Limit(Goal goal, string text = LimitText, string? provider = null, TaskSpec? task = null,
            string? model = null, bool stdout = false)
        {
            task ??= goal.Tasks.Single();
            var agent = Agents.First(agent => agent.Id == task.AssignedAgentId);
            var dispatch = new TaskDispatchRecord(agent.Subscription!.WorkerProfileName, "fixture worker",
                Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                ProviderName: provider ?? agent.Model.ProviderName, GoalId: goal.Id,
                ModelName: model,
                AssignedAgentId: agent.Id.Value,
                WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile(agent.Subscription.WorkerProfileName).Identity.Kind);
            Kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            Kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(dispatch.Command,
                dispatch.WorkingDirectory, 1, stdout ? text : "", stdout ? "" : text, Clock.UtcNow,
                DispatchStartedAt: dispatch.DispatchedAt, StandardErrorPath: Receipt(goal)));
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
        }

        internal ClaudeCliAuthState ProbeAuth()
        {
            AuthProbes++;
            return DispatcherProviderProbeFakes.SignedInClaudeCli();
        }

        internal bool CommandExists(string _) => true;

        internal WorkerSubscriptionPreflightResult Preflight(Goal goal, TaskSpec task, IEnumerable<Goal> holds)
        {
            var result = WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, Agents, Profiles,
                Workspace.ResolveExecutionDirectory(goal.Id), Clock.UtcNow,
                allowGitReference: true, sandboxOptions: Sandbox, commandExists: CommandExists,
                providerHoldScope: holds, cascadeTesterCheapFirst: false, claudeAuthProbe: ProbeAuth);
            Preflights.Add((task.AssignedAgentId, result.Allowed));
            return result;
        }

        internal ProviderHoldAgentSubstitution Resolver() => new(Kernel, Agents, Preflight, () => Clock.UtcNow);
        internal ProviderHoldAgentSubstitution.Result Resolve(Goal goal) => Resolver().Resolve(goal, goal.Tasks.Single());

        internal SubscriptionStartResult Start(Goal goal,
            Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process> launcher,
            IReadOnlySet<TaskId>? excludedTaskIds = null)
        {
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            return new GoalDispatchOperations().StartSubscriptionReadyTasks(Kernel, Workspace, goal, Agents, Profiles,
                checkpointBeforeWorkerStart: (kernel, _, _, _) => repository.SaveAsync(kernel).GetAwaiter().GetResult(),
                runner: new BackgroundDispatchRunner(clock: Clock, disableProcessStart: false, startProcess: launcher),
                sandboxOptions: Sandbox, commandExists: CommandExists, claudeAuthProbe: ProbeAuth,
                cascadeTesterCheapFirst: false, utcNow: () => Clock.UtcNow, excludedTaskIds: excludedTaskIds);
        }

        internal static AgentDefinition Agent(string id, string provider, string profile, string model) => new(
            new AgentId(id), id, AgentRole.Planner,
            new ModelProfile(provider, model, ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse,
                SubscriptionMode.ApiKey, "high"), ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profile, model, "high"), IsProviderRoutingConstrained: true);

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    internal sealed class CooldownClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2030-10-27T12:00:00Z");
    }
}
