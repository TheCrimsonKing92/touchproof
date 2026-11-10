using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned database, journal and repository; no external processes except Git.
public sealed class OperatorIntentIntegrationScopeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrdinaryScopePrefixQuestion_AppliesAnswer(bool openRound, bool taskScoped)
    {
        using var fixture = new IntegrationScopeFixture();
        var goal = openRound ? fixture.Goal : fixture.Kernel.CreateGoal("Ordinary question",
            [new(TaskId.New(), "Implement", AgentRole.Developer)]);
        if (!openRound) fixture.Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = fixture.Kernel.RequestHumanInput(goal.Id, taskScoped ? goal.Tasks.First().Id : null,
            "Integration scope confirmation required. May I edit src/Other.cs?");
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
            request.Id.Value, goal.Id.Value, "Explain the adjacent correction first", OperatorActorKind.Human);
        var answer = await fixture.Store.EnqueueAsync(fixture.Intent() with
        {
            Verb = OperatorIntentVerbs.Answer, GoalId = goal.Id.Value,
            PayloadJson = JsonSerializer.Serialize(payload, OperatorIntentJson.Options)
        });
        fixture.Intents.ExecutePending(fixture.Kernel, goal);
        fixture.Intents.CompletePersisted([goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(answer.Id))!.Status);
        Assert.True(request.IsCompleted);
        Assert.Equal(payload.Text, request.Answer);
    }

    [Fact]
    public async Task TypedScopeRequirement_RejectsProse_ResolvesOnlyRecordedIdentity()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.RequireScopeAfterAnswer(fixture.Goal, fixture.Base.Developer.Id.Value);
        Assert.False(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var scope = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.Equal("integration-scope:" + fixture.RoundId, scope.QuestionFingerprint);
        var ordinary = fixture.Kernel.RequestHumanInput(fixture.Goal.Id, null, scope.Question);
        Assert.NotEqual(scope.QuestionFingerprint, ordinary.QuestionFingerprint);
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
            scope.Id.Value, fixture.Goal.Id.Value, "Approved", OperatorActorKind.Human);
        var answer = fixture.Intent() with { Verb = OperatorIntentVerbs.Answer,
            PayloadJson = JsonSerializer.Serialize(payload, OperatorIntentJson.Options) };
        var rejected = await fixture.Apply(answer);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains("typed integration-scope command", rejected.Outcome);
        Assert.False(scope.IsCompleted);
        Assert.False(ordinary.IsCompleted);

        Assert.Equal(OperatorIntentStatus.Applied,
            (await fixture.Apply(fixture.Intent(IntegrationScopeAction.OriginalOnly))).Status);
        Assert.True(scope.IsCompleted);
        Assert.False(ordinary.IsCompleted);
        Assert.Equal(ordinary.Id, Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted)).Id);
    }

    [Fact]
    public void RevocationAfterResolvedRequirement_CreatesOneNewPendingRequirement()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.RequireScopeAfterAnswer(fixture.Goal, fixture.Base.Developer.Id.Value);
        Assert.False(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var original = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent(IntegrationScopeAction.OriginalOnly));
        Assert.True(original.IsCompleted);
        var approve = fixture.Intent();
        fixture.Owner.ApplyScope(fixture.Goal, approve);
        fixture.Owner.ApplyScope(fixture.Goal, fixture.Intent(IntegrationScopeAction.Revoke, approvalId: approve.Id));
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var current = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.NotEqual(original.Id, current.Id);
        Assert.NotEqual(original.BlockerFingerprint, current.BlockerFingerprint);
        fixture.Base.GitFixture.AssertRestored();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableEffectBeforeIntentCompletion_ReplaysAcrossCoordinatorRestart(bool beforeCheckpoint)
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = await fixture.Store.EnqueueAsync(fixture.Intent(paths: ["src\\Other.cs", "src/Other.cs"]));
        var snapshot = fixture.Kernel.ExportSnapshot();
        fixture.Intents.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        var before = fixture.Base.Entry.Detail;
        // No CompletePersisted: recover the journal effect with a fresh coordinator.
        var kernel = AgentOrchestratorKernel.FromSnapshot(beforeCheckpoint ? snapshot : fixture.Kernel.ExportSnapshot());
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var restarted = fixture.Coordinator(kernel);
        restarted.ExecutePending(kernel, goal);
        restarted.CompletePersisted([fixture.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(before, fixture.Base.Entry.Detail);
        using var round = JsonDocument.Parse(before!);
        var decision = Assert.Single(round.RootElement.GetProperty("ScopeDecisions").EnumerateArray());
        Assert.Equal(intent.Id, decision.GetProperty("IntentId").GetString());
        Assert.Equal(intent.IdempotencyKey, decision.GetProperty("IdempotencyKey").GetString());
        Assert.Equal("operator", decision.GetProperty("Actor").GetString());
        Assert.Equal(new[] { "src/Other.cs" }, decision.GetProperty("Request").GetProperty("Paths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "src/Widget.cs" }, round.RootElement.GetProperty("ConflictPaths").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(new[] { "src/Other.cs" }, fixture.Reload(kernel).IntegrateMainBeforeDeveloperDispatch(goal).ApprovedAdditionalPaths);
    }

    [Fact]
    public async Task DuplicateDeliveryAndConflictingIdempotency_PreserveOneEffect()
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = fixture.Intent();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(intent)).Status);
        Assert.Equal(intent.Id, (await fixture.Store.EnqueueAsync(intent)).Id);
        var before = fixture.Base.Entry.Detail;
        fixture.Reload().ApplyScope(fixture.Goal, intent);
        Assert.Equal(before, fixture.Base.Entry.Detail);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.EnqueueAsync(intent with { PayloadJson = "{}" }));
        Assert.Equal(before, fixture.Base.Entry.Detail);
    }

    [Fact]
    public async Task AnswerProse_RequiresTypedDecision_AndOriginalOnlyClearsOneRequirement()
    {
        using var fixture = new IntegrationScopeFixture();
        var request = fixture.Kernel.RequestHumanInput(fixture.Goal.Id, fixture.Base.Developer.Id, "May I edit the adjacent seam?");
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, request.Id.Value,
            fixture.Goal.Id.Value, "Approved: edit src/Other.cs", OperatorActorKind.Human);
        var answer = fixture.Intent() with { Verb = OperatorIntentVerbs.Answer,
            PayloadJson = JsonSerializer.Serialize(payload, OperatorIntentJson.Options) };
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(answer)).Status);
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        Assert.Equal(0, fixture.Base.InnerCalls);
        var scope = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.Contains(fixture.RoundId, scope.Question);
        Assert.Contains(fixture.Base.GitFixture.MainHead, scope.Question);
        var confirm = fixture.Intent(IntegrationScopeAction.OriginalOnly);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(confirm)).Status);
        Assert.Empty(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        var admission = fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal);
        Assert.True(admission.CanDispatch);
        Assert.Empty(admission.ApprovedAdditionalPaths);
        fixture.Base.Resolve(outside: true);
        Assert.Contains("src/Other.cs", fixture.Reload().Validate(fixture.Goal));
    }

    [Fact]
    public async Task ForgedTimelineMarker_CannotCompleteIntentWithoutRoundAuthority()
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = fixture.Intent();
        fixture.Kernel.RecordGoalPolicyDecision(fixture.Goal.Id, OperatorIntentCoordinator.BuildApplicationMarker(intent));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(intent)).Status);
        Assert.Equal(new[] { "src/Other.cs" }, fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).ApprovedAdditionalPaths);
    }

    [Fact]
    public async Task RetiredIntentReplayAcrossRestart_ClosesOnlyObsoleteWait()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.RequireScopeAfterAnswer(fixture.Goal, fixture.Base.Developer.Id.Value);
        Assert.False(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var pending = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        var snapshot = fixture.Kernel.ExportSnapshot();
        var intent = await fixture.Store.EnqueueAsync(fixture.Intent());
        fixture.Intents.ExecutePending(fixture.Kernel, fixture.Goal);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Store.GetAsync(intent.Id))!.Status);
        var detail = fixture.Base.Entry.Detail!;
        GoalOperationJournal.Failed(fixture.Base.GitFixture.Repository, fixture.Goal,
            ConductorConflictIntegration.OperationName, detail);
        // Crash after the journal effect/retirement, before kernel checkpoint and intent completion.
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var recovered = Assert.Single(kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.Equal(pending.Id, recovered.Id);
        var coordinator = fixture.Coordinator(kernel);

        coordinator.ExecutePending(kernel, goal);
        coordinator.CompletePersisted([goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        Assert.True(recovered.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, recovered.AuthoritativeAnswer!.Origin);
        Assert.Contains("no path authority transferred", recovered.Answer);
        Assert.Equal(GoalOperationStatus.Failed, fixture.Base.Entry.Status);
        Assert.Equal(detail, fixture.Base.Entry.Detail);
        Assert.False(fixture.Reload(kernel).IntegrateMainBeforeDeveloperDispatch(goal).CanDispatch);
        fixture.Base.GitFixture.AssertRestored();
    }

    [Fact]
    public void RequirementWithoutRecordedRound_RemainsPending()
    {
        using var fixture = new IntegrationScopeFixture();
        var missingId = Guid.NewGuid().ToString("N");
        var request = fixture.Kernel.RequestHumanInputDeduplicated(fixture.Goal.Id, null,
            "Missing scope authority", kind: HumanWaitKind.Other, isAutoDefaultable: false, isDismissible: false,
            questionFingerprint: "integration-scope:" + missingId,
            blockerFingerprint: "integration-scope:" + missingId + ":initial").Request;

        fixture.Owner.ReconcileScopeRequirements(fixture.Goal);

        Assert.False(request.IsCompleted);
        Assert.Equal(GoalStatus.WaitingForHuman, fixture.Goal.Status);
        Assert.Equal(GoalOperationStatus.Begin, fixture.Base.Entry.Status);
        fixture.Base.GitFixture.AssertRestored();
    }
}
