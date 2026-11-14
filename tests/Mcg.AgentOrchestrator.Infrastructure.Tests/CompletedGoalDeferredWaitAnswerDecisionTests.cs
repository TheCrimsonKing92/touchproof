using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: in-memory kernels and a private temporary SQLite store per service case.
public sealed class CompletedGoalDeferredWaitAnswerDecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EligibleAnswer_ResolvesExplicitOrSingleBareTarget(bool bare)
    {
        var (kernel, goal, waits) = Setup();
        var intent = Answer(goal, bare ? "" : waits[0].Id.Value);

        var decision = Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.Apply>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, intent));

        Assert.Same(waits[0], decision.Wait);
        Assert.Equal("Observed the landed behavior", decision.Payload.Text);
        Assert.False(waits[0].IsCompleted); // Deciding alone must not apply the answer.
    }

    [Fact]
    public void TwoWaits_ExplicitIdSelectsOnlyNamedWait()
    {
        var (kernel, goal, waits) = Setup(2);
        var decision = Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.Apply>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, waits[1].Id.Value)));

        Assert.Same(waits[1], decision.Wait);
        Assert.All(waits, wait => Assert.False(wait.IsCompleted));
    }

    [Fact]
    public void TwoWaits_BareAnswerReturnsEveryOpenId()
    {
        var (kernel, goal, waits) = Setup(2);
        var decision = Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.Ambiguous>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, "")));

        Assert.Equal(waits.Select(wait => wait.Id.Value).Order(), decision.WaitIds.Order());
        Assert.All(waits, wait => Assert.False(wait.IsCompleted));
    }

    [Theory]
    [InlineData("post-implementation", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData("retrievable", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(PostLandingOperatorCheck.Availability, HumanWaitKind.SpecClarification)]
    public void OtherWaits_AreNotEligible(string availability, HumanWaitKind kind)
    {
        var (kernel, goal, waits) = Setup();
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = [snapshot.HumanInputRequests.Single() with
            {
                Kind = kind,
                PlannerEvidence = waits[0].PlannerEvidence! with { Availability = availability }
            }]
        });
        goal = kernel.GetGoal(goal.Id);

        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, waits[0].Id.Value)));
        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, "")));
    }

    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Superseded)]
    public void OtherGoalStatuses_KeepExistingAnswerPath(GoalStatus status)
    {
        var (kernel, goal, waits) = Setup();
        kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = status });
        goal = kernel.GetGoal(goal.Id);

        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, waits[0].Id.Value)));
    }

    [Theory]
    [InlineData("verb")]
    [InlineData("malformed")]
    [InlineData("null")]
    [InlineData("goal")]
    [InlineData("actor")]
    [InlineData("route")]
    [InlineData("amendment")]
    [InlineData("empty-text")]
    [InlineData("clarification")]
    [InlineData("unknown-kind")]
    [InlineData("unknown-id")]
    public void InvalidAnswer_DoesNotTakeTerminalException(string invalid)
    {
        var (kernel, goal, waits) = Setup();
        var intent = Answer(goal, waits[0].Id.Value);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        payload = invalid switch
        {
            "goal" => payload with { GoalId = "another-goal" },
            "actor" => payload with { ActorKind = OperatorActorKind.Agent },
            "route" => payload with { RouteRole = "developer" },
            "amendment" => payload with { AmendmentDisposition = PlannerBriefAmendmentDisposition.Decline },
            "empty-text" => payload with { Text = "  " },
            "clarification" => payload with { TargetKind = OperatorAnswerTargetKind.Clarification, TargetId = "" },
            "unknown-kind" => payload with { TargetKind = (OperatorAnswerTargetKind)99, TargetId = "" },
            "unknown-id" => payload with { TargetId = "missing" },
            _ => payload
        };
        intent = intent with
        {
            Verb = invalid == "verb" ? OperatorIntentVerbs.GoalNote : intent.Verb,
            PayloadJson = invalid == "malformed" ? "{" : invalid == "null" ? "null" :
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options)
        };

        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, intent));
        Assert.False(waits[0].IsCompleted);
    }

    [Fact]
    public void ClosedTarget_ExplicitAndBareAnswersAreNotEligible()
    {
        var (kernel, goal, waits) = Setup();
        kernel.SubmitHumanInput(waits[0].Id, "Already observed");

        foreach (var target in new[] { waits[0].Id.Value, "" })
            Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
                CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, Answer(goal, target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_AnswersWaitAndConfirmsOnlyAfterReload(bool bare)
    {
        using var fixture = new ServiceFixture();
        var (kernel, goal, waits) = Setup(bare ? 1 : 2);
        var intent = await fixture.Store.EnqueueAsync(Answer(goal, bare ? "" : waits[0].Id.Value));
        var before = kernel.ExportSnapshot();

        var applied = fixture.Service(kernel, goal, [intent]);

        Assert.True(applied.Mutated);
        Assert.Contains(intent.Id, applied.HandledIntentIds);
        Assert.True(waits[0].IsCompleted);
        Assert.Equal("Observed the landed behavior", waits[0].Answer);
        Assert.Equal(HumanInputAnswerOrigin.Operator, waits[0].AuthoritativeAnswer!.Origin);
        Assert.All(waits.Skip(1), wait => Assert.False(wait.IsCompleted));
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Single(goal.Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId == intent.Id));

        // A reload without the new checkpoint cannot confirm the intent.
        var unsaved = AgentOrchestratorKernel.FromSnapshot(before);
        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.Apply>(
            CompletedGoalDeferredWaitAnswer.Decide(unsaved, unsaved.GetGoal(goal.Id), intent));

        var reloaded = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var confirmed = fixture.Service(reloaded, reloaded.GetGoal(goal.Id), [intent]);
        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.False(confirmed.Mutated);
        Assert.Contains(intent.Id, confirmed.HandledIntentIds);
        Assert.Equal(OperatorIntentStatus.Applied, stored.Status);
        Assert.DoesNotContain("goal-terminal-evicted", stored.Outcome!, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Completed, reloaded.GetGoal(goal.Id).Status);
        var durableWait = reloaded.HumanInputRequests.Single(wait => wait.Id == waits[0].Id);
        Assert.True(durableWait.IsCompleted);
        Assert.Equal("Observed the landed behavior", durableWait.Answer);
        Assert.Single(reloaded.GetGoal(goal.Id).Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId == intent.Id));
    }

    [Fact]
    public async Task Service_AmbiguousBareAnswerRejectsWithIdsAndDistinctCode()
    {
        using var fixture = new ServiceFixture();
        var (kernel, goal, waits) = Setup(2);
        var intent = await fixture.Store.EnqueueAsync(Answer(goal, ""));

        var result = fixture.Service(kernel, goal, [intent]);

        Assert.False(result.Mutated);
        Assert.Contains(intent.Id, result.HandledIntentIds);
        var stored = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, stored.Status);
        Assert.Contains("reasonCode=completed-goal-deferred-wait-ambiguous", stored.Outcome!, StringComparison.Ordinal);
        Assert.DoesNotContain("goal-terminal-evicted", stored.Outcome!, StringComparison.Ordinal);
        Assert.All(waits, wait =>
        {
            Assert.Contains(wait.Id.Value, stored.Outcome!, StringComparison.Ordinal);
            Assert.False(wait.IsCompleted);
        });
    }

    [Fact]
    public async Task Service_SecondAnswerToSameWaitRemainsForExistingRejection()
    {
        using var fixture = new ServiceFixture();
        var (kernel, goal, waits) = Setup();
        var first = await fixture.Store.EnqueueAsync(Answer(goal, waits[0].Id.Value));
        var second = await fixture.Store.EnqueueAsync(Answer(goal, waits[0].Id.Value));

        var result = fixture.Service(kernel, goal, [first, second]);

        Assert.Equal(new[] { first.Id }, result.HandledIntentIds);
        Assert.IsType<CompletedGoalDeferredWaitAnswer.Decision.NotApplicable>(
            CompletedGoalDeferredWaitAnswer.Decide(kernel, goal, second));
        Assert.Equal(OperatorIntentStatus.Pending, (await fixture.Store.GetAsync(second.Id))!.Status);
        Assert.Single(goal.Timeline.Where(entry => entry.OperatorIntentApplied is not null));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, HumanInputRequest[] Waits) Setup(int count = 1)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Answer a deferred check after completion");
        for (var index = 0; index < count; index++)
            PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability,
                needed: $"Observe independent behavior {index}");
        kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id) with { Status = GoalStatus.Completed });
        goal = kernel.GetGoal(goal.Id);
        Assert.Empty(goal.CriterionEvidenceObligations);
        return (kernel, goal, kernel.HumanInputRequests.ToArray());
    }

    private static OperatorIntentRecord Answer(Goal goal, string targetId) => new(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
        JsonSerializer.Serialize(new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
            targetId, goal.Id.Value, "Observed the landed behavior", OperatorActorKind.Human), OperatorIntentJson.Options),
        [], "operator", "cli", "local-process", DateTimeOffset.UtcNow);

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"completed-wait-{Guid.NewGuid():N}");
        internal SqliteOperatorIntentStore Store { get; }
        private readonly OperatorIntentCoordinator _coordinator;
        private readonly ConductorParallelAcceptanceAttemptCoordinator _attempts;

        internal ServiceFixture()
        {
            Directory.CreateDirectory(_root);
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            _coordinator = new OperatorIntentCoordinator(Store);
            _attempts = new ConductorParallelAcceptanceAttemptCoordinator(_root, "main");
        }

        internal CompletedGoalDeferredWaitAnswer.ServiceResult Service(
            AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<OperatorIntentRecord> batch) =>
            CompletedGoalDeferredWaitAnswer.Service(kernel, goal, _coordinator, _attempts, batch);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
