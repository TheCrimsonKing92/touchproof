using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Assert = Xunit.Assert;

// Parallel-safe: in-memory goals, private temp stores, a fake clock and preserved fake Author output.
// No worker, gate, build or git process is started.
public sealed class AuthorRecommendationApplicationTests
{
    private const string Question = "Which runtime should be used?";
    private const string Recommendation = "Use Windows.\nKeep the current runtime version.";
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    [Xunit.Fact]
    public async Task MissingEvidence_AtDeadline_AppliesAuditedAnswerAndClearsHold()
    {
        using var fixture = new Fixture();
        await fixture.EscalateFromAuthor();
        var started = fixture.Goal.CurrentHold!.StartedAt;
        fixture.Now = started + Wait - TimeSpan.FromSeconds(1);
        fixture.Tick();
        Assert.Empty(await fixture.Answers());
        Assert.Empty(fixture.ApplicationEvents());
        Assert.Equal(started, fixture.Goal.CurrentHold.StartedAt);

        fixture.Now = started + Wait;
        fixture.Tick();
        var intent = Assert.Single(await fixture.Answers());
        var answer = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(OperatorActorKind.Agent, intent.ActorKind);
        Assert.Equal("author", intent.Actor);
        Assert.Equal("conductor-author", intent.Channel);
        Assert.Equal("author", intent.AuthenticationAssurance);
        Assert.Equal(OperatorActorKind.Agent, answer.ActorKind);
        Assert.Equal(OperatorAnswerTargetKind.Clarification, answer.TargetKind);
        Assert.Equal(fixture.Item.Id, answer.TargetId);
        Assert.Equal($"AUTHOR_RECOMMENDATION_APPLIED\nquestion={Question}\nreason=missing-evidence\n" +
            $"class=reversible\nwait-seconds=300\n{Recommendation}", answer.Text);
        Assert.Contains($"author-item={fixture.Identity}", answer.EvidenceReferences!);

        var applied = fixture.ApplyPending();
        Assert.True(applied.MutatedGoalState);
        Assert.False(applied.PreserveGoalHold);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Equal(CollaborationItemStatus.Resolved, (await fixture.Reload()).Status);
        var detail = Assert.Single(fixture.ApplicationEvents());
        Assert.Equal($"item={fixture.Identity} kind=recommendation-applied reason=missing-evidence " +
            $"class=reversible wait-seconds=300 intent={intent.Id}", detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(299)]
    [Xunit.InlineData(300)]
    [Xunit.InlineData(3000)]
    public async Task RecordedHumanAnswer_AtAnyDeadline_PreventsApplication(int seconds)
    {
        using var fixture = new Fixture();
        await fixture.EscalateFromAuthor();
        var hold = fixture.Goal.CurrentHold;
        fixture.Now = hold!.StartedAt + TimeSpan.FromSeconds(seconds);
        await fixture.EnqueueHumanAnswer();
        fixture.Tick();
        Assert.Equal(OperatorActorKind.Human, Assert.Single(await fixture.Answers()).ActorKind);
        Assert.Empty(fixture.ApplicationEvents());
        Assert.Equal(hold, fixture.Goal.CurrentHold);
    }

    [Xunit.Theory]
    [Xunit.InlineData("acceptance-weakening", Recommendation)]
    [Xunit.InlineData("frozen-fact-ruling-unsupported-target", Recommendation)]
    [Xunit.InlineData("irreversible-action", Recommendation)]
    [Xunit.InlineData("missing-evidence", "")]
    [Xunit.InlineData("model-ask-owner", "   ")]
    public async Task IneligibleQuestion_AfterTenWaits_RemainsHeld(string reason, string recommendation)
    {
        using var fixture = new Fixture();
        await fixture.RaiseQuestion(reason, recommendation);
        var hold = fixture.Goal.CurrentHold;
        fixture.Now += Wait * 10;
        fixture.Tick();
        Assert.Empty(await fixture.Answers());
        Assert.Empty(fixture.ApplicationEvents());
        Assert.Equal(hold, fixture.Goal.CurrentHold);
        Assert.Equal(CollaborationItemStatus.Raised, (await fixture.Reload()).Status);
    }

    [Xunit.Fact]
    public void WhitespaceRecommendation_IsIneligible()
    {
        var now = DateTimeOffset.UnixEpoch;
        var question = new AuthorOwnerQuestionHold("Clarification", "id", "missing-evidence",
            Question, " \n ", OwnerQuestionReversibility.Reversible);
        Assert.False(AuthorRecommendationApplication.IsEligible(question, now, now + Wait * 10, Wait));
    }

    [Xunit.Fact]
    public async Task LaterTicksAndHostRestart_WriteOneAnswerAndOneEvent()
    {
        using var fixture = new Fixture();
        await fixture.EscalateFromAuthor();
        fixture.Now += Wait;
        fixture.RestartHost(); // Original persisted hold time, with no in-memory timer.
        fixture.Tick();
        Assert.Single(await fixture.Answers());
        Assert.Single(fixture.ApplicationEvents());
        for (var i = 0; i < 3; i++)
        {
            fixture.Now += Wait;
            fixture.Tick();
        }
        fixture.ApplyPending();
        fixture.RestartHost();
        fixture.Tick();
        Assert.Single(await fixture.Answers());
        Assert.Single(fixture.ApplicationEvents());

        await fixture.EnqueueHumanAnswer();
        var later = (await fixture.Answers()).Single(record => record.ActorKind == OperatorActorKind.Human);
        var laterAnswer = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(later.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(fixture.Item.Id, laterAnswer.TargetId);
        Assert.Equal("Use Linux instead.", laterAnswer.Text);
        Assert.Equal(2, (await fixture.Answers()).Count);
        Assert.Single(fixture.ApplicationEvents());
    }

    [Xunit.Fact]
    public async Task EffectivePolicyWait_IsUsedAndRecorded()
    {
        using var fixture = new Fixture();
        await fixture.RaiseQuestion("model-ask-owner", Recommendation);
        fixture.EffectiveWait = TimeSpan.FromSeconds(30);
        fixture.Now += TimeSpan.FromSeconds(30);
        fixture.Tick();
        var intent = Assert.Single(await fixture.Answers());
        Assert.Contains("wait-seconds=30", intent.PayloadJson);
        Assert.Contains("wait-seconds=30", Assert.Single(fixture.ApplicationEvents()));
    }

    [Xunit.Theory]
    [Xunit.InlineData("disabled")]
    [Xunit.InlineData("cleared")]
    [Xunit.InlineData("different-hold")]
    [Xunit.InlineData("unavailable-policy")]
    [Xunit.InlineData("other-goal")]
    [Xunit.InlineData("cancelled")]
    [Xunit.InlineData("parked")]
    public async Task InactiveOrChangedContext_DoesNotApply(string context)
    {
        using var fixture = new Fixture();
        await fixture.EscalateFromAuthor();
        fixture.Now += Wait * 10;
        switch (context)
        {
            case "disabled": fixture.Host.Enabled = false; break;
            case "cleared": fixture.Kernel.ClearGoalHold(fixture.Goal.Id); break;
            case "different-hold": fixture.Kernel.ObserveGoalHold(fixture.Goal.Id, "other", "Other hold",
                fixture.Now, TimeSpan.MaxValue, "other"); break;
            case "unavailable-policy": fixture.EffectiveWait = null; break;
            case "cancelled": fixture.Kernel.CancelGoal(fixture.Goal.Id, "Cancelled by owner."); break;
            case "parked": fixture.Kernel.ParkGoal(fixture.Goal.Id, "Parked by owner."); break;
        }
        fixture.Host.ServiceTick(fixture.Kernel, context == "other-goal" ? "another-goal" : null);
        Assert.Empty(await fixture.Answers());
        Assert.Empty(fixture.ApplicationEvents());
    }

    [Xunit.Fact]
    public void PolicyFile_DefaultAndOverride_LoadEffectiveWait()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"author-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(Wait, AuthorRecommendationApplication.LoadWait(directory));
            var path = Path.Combine(directory, "conductor-policy.json");
            File.WriteAllText(path, (Mcg.AgentOrchestrator.Core.Conductor.ConductorAutonomyPolicy.Permissive with
                { AuthorRecommendationWaitSeconds = 19 }).ToJson());
            Assert.Equal(TimeSpan.FromSeconds(19), AuthorRecommendationApplication.LoadWait(directory));
            File.WriteAllText(path, "malformed");
            Assert.Null(AuthorRecommendationApplication.LoadWait(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"author-recommendation-{Guid.NewGuid():N}");
        private readonly OrchestratorWorkspace _workspace;
        private readonly CollaborationItemStore _collaboration;
        private readonly ConductorAuthorClaimStore _claims;
        private readonly SpecRefinerPrecedentStore _precedents;
        private readonly GoalRefinementService _refinement;
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        internal TimeSpan? EffectiveWait { get; set; } = Wait;
        internal Goal Goal { get; }
        internal CollaborationItem Item { get; private set; } = null!;
        internal SqliteOperatorIntentStore Intents { get; }
        internal ConductorAuthorHost Host { get; private set; }
        internal string Identity => $"{Goal.Id.Value}:Clarification:{Item.Id}";

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _workspace = OrchestratorWorkspace.ForDirectory(_root);
            _collaboration = CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory);
            _claims = new ConductorAuthorClaimStore(Path.Combine(_workspace.OrchestratorDirectory, "author-claims.db"));
            _precedents = new SpecRefinerPrecedentStore(_workspace.SpecRefinerPrecedentsPath);
            Intents = SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory);
            _refinement = new GoalRefinementService(new InMemoryModelProviderRegistry([]),
                ModelFunctionCatalog.Empty, _collaboration, _precedents);
            Goal = Kernel.CreateGoal("Choose a runtime.", [new TaskSpec(TaskId.New(), "Plan runtime", AgentRole.Planner)]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Host = CreateHost();
        }

        private ConductorAuthorHost CreateHost() => new(_claims, _collaboration, new UnexpectedModel(),
            Intents, _precedents, _ => _root, new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
            new ConductEventLogWriter(_workspace.ConductEventsLogPath, utcNow: () => Now),
            utcNow: () => Now, recommendationWait: () => EffectiveWait);

        internal void RestartHost() { Host.Stop(); Host = CreateHost(); }
        internal void Tick() => Host.ServiceTick(Kernel);

        private async Task CreateQuestion()
        {
            Item = await _collaboration.RaiseAsync(CollaborationItemType.Clarification, Goal.Id.Value,
                "runtime", $"Question: {Question}\nFork kind: runtime", $"spec-clarification:{Goal.Id.Value}:runtime");
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec(Goal.Objective, ["Choose a runtime."],
                VerificationClass.TestVerifiable, [],
                [new RefinedSpecOpenQuestion(Item.CorrelationKey!, Question, "runtime", "Open")]));
        }

        internal async Task EscalateFromAuthor()
        {
            await CreateQuestion();
            var item = new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification,
                Item.Id, Goal.Id.Value, Question, "runtime");
            Assert.True(_claims.TryClaim(item, Now));
            _claims.Preserve(item, JsonSerializer.Serialize(new { kind = "answer", text = Recommendation,
                evidenceReferences = Array.Empty<string>() }));
            Tick();
            Assert.Equal("missing-evidence", AuthorOwnerQuestionHold.TryParse(Goal.CurrentHold)!.Reason);
            Assert.Empty(await Answers());
        }

        internal async Task RaiseQuestion(string reason, string recommendation)
        {
            await CreateQuestion();
            new ConductorAuthorOwnerQuestion(Goal.Id.Value, OperatorAnswerTargetKind.Clarification,
                Item.Id, Question, recommendation, reason).Raise(Kernel, Goal,
                new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory),
                new ConductEventLogWriter(_workspace.ConductEventsLogPath), Now, new HashSet<GoalId>());
        }

        internal async Task<IReadOnlyList<OperatorIntentRecord>> Answers() =>
            (await Intents.ListForGoalAsync(Goal.Id.Value, int.MaxValue)).Where(i => i.Verb == OperatorIntentVerbs.Answer).ToArray();
        internal async Task<CollaborationItem> Reload() =>
            (await _collaboration.ListAsync(Goal.Id.Value)).Single(item => item.Id == Item.Id);

        internal OperatorIntentExecutionResult ApplyPending()
        {
            var coordinator = new OperatorIntentCoordinator(Intents, decisions: _collaboration,
                goalStateVersionResolver: _ => 0,
                clarificationAnswers: (correlation, text, version) =>
                    _refinement.TryResolveOpenClarificationAsync(correlation, text, version));
            var applied = OperatorIntentGoalApplication.ApplyPending(Kernel, Goal, coordinator,
                new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.Combine(_workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
                    TrunkBranchName.Default, _workspace.ExecutionDirectory)).Result;
            coordinator.CompletePersisted([Goal.Id]);
            return applied;
        }

        internal Task<OperatorIntentRecord> EnqueueHumanAnswer()
        {
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification,
                Item.Id, Goal.Id.Value, "Use Linux instead.", OperatorActorKind.Human, [], null);
            return Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, Goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "owner", "cli", "local-process", Now));
        }

        internal string[] ApplicationEvents() => !File.Exists(_workspace.ConductEventsLogPath) ? [] :
            File.ReadAllLines(_workspace.ConductEventsLogPath).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("detail").GetString()!;
            }).Where(detail => detail.Contains("kind=recommendation-applied", StringComparison.Ordinal)).ToArray();

        public void Dispose() { Host.Stop(); Directory.Delete(_root, recursive: true); }
    }

    private sealed class UnexpectedModel : IConductorAuthorModelRound
    {
        public Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected model dispatch.");
    }
}
