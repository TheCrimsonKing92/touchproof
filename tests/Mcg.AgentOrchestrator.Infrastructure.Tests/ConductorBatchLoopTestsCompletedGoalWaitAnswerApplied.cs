using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its SQLite stores; the loop uses semantic callbacks and no workers.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsCompletedGoalWaitAnswerApplied(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const int CompletedWaitAnswerHangFailsafeMilliseconds = 30_000;

    [Theory(Timeout = CompletedWaitAnswerHangFailsafeMilliseconds)]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 1, true)]
    [Trait("Category", "CrossTick")]
    public async Task Answer_AfterCompletion_PersistsWaitBeforeConfirmingIntent(
        bool bare, int waitCount, bool validatedDuty)
    {
        using var fixture = await Fixture.Create(waitCount, validatedDuty: validatedDuty);
        var target = fixture.Waits.Last();
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.False(target.IsCompleted);
        Assert.Equal(validatedDuty, fixture.Goal.CriterionEvidenceObligations.Any(item => item.IsValidatedPostLanding));
        var intent = await fixture.Store.EnqueueAsync(fixture.Answer(bare ? "" : target.Id.Value));
        var loads = new List<(bool Completed, OperatorIntentStatus Status)>();
        var persisted = new List<OperatorIntentStatus>();
        var ticks = new List<BatchTickSummary>();
        var goalWorkCalls = new List<string>();

        // Facts are also read by completed-dependency bookkeeping outside the goal walk.
        // Observe lifecycle actions and tick outcomes to detect admission to goal work.
        T UnexpectedGoalWork<T>(Goal goal)
        {
            goalWorkCalls.Add(goal.Id.Value);
            throw new InvalidOperationException("Completed goal entered the working set.");
        }

        var result = fixture.Run(MakeDriver(
                createWorkspace: UnexpectedGoalWork<string>,
                dispatchAndStart: UnexpectedGoalWork<DispatchStartOutcome>,
                runAcceptance: UnexpectedGoalWork<bool>,
                land: UnexpectedGoalWork<LandingResult>,
                record: goal => goalWorkCalls.Add(goal.Id.Value),
                cleanup: UnexpectedGoalWork<GoalWorktreeRemoveResult>),
            iterations: 2,
            onTick: ticks.Add,
            onLoad: loaded => loads.Add((loaded.HumanInputRequests.Single(wait => wait.Id == target.Id).IsCompleted,
                fixture.Store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status)),
            onPersist: current =>
            {
                var wait = current.HumanInputRequests.Single(wait => wait.Id == target.Id);
                Assert.True(wait.IsCompleted);
                Assert.Equal(Fixture.AnswerText, wait.Answer);
                Assert.Equal(GoalStatus.Completed, current.GetGoal(fixture.Goal.Id).Status);
                persisted.Add(fixture.Store.GetAsync(intent.Id).GetAwaiter().GetResult()!.Status);
            });

        Assert.Equal(2, result.Ticks);
        Assert.Equal("max-iter", result.StopReason);
        Assert.False(result.StopRequested);
        Assert.Equal(new[] { (false, OperatorIntentStatus.Pending), (true, OperatorIntentStatus.Pending) }, loads);
        Assert.Empty(goalWorkCalls);
        Assert.Equal(2, ticks.Count);
        Assert.All(ticks, tick =>
        {
            Assert.Equal(0, tick.Advanced);
            Assert.Equal(0, tick.Escalated);
            Assert.Equal(0, tick.Retried);
            Assert.Equal(0, tick.Done);
        });
        Assert.NotEmpty(persisted);
        Assert.Equal(OperatorIntentStatus.Pending, persisted[0]);
        var storedIntent = (await fixture.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, storedIntent.Status);
        Assert.DoesNotContain("goal-terminal-evicted", storedIntent.Outcome!, StringComparison.Ordinal);
        Assert.Contains(target.Id.Value, storedIntent.Outcome!, StringComparison.Ordinal);
        var durable = await fixture.Reload();
        var answered = durable.HumanInputRequests.Single(wait => wait.Id == target.Id);
        Assert.True(answered.IsCompleted);
        Assert.Equal(Fixture.AnswerText, answered.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Operator, answered.AuthoritativeAnswer!.Origin);
        Assert.All(durable.HumanInputRequests.Where(wait => wait.Id != target.Id), wait => Assert.False(wait.IsCompleted));
        var goal = durable.GetGoal(fixture.Goal.Id);
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Single(goal.Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId == intent.Id));
        if (validatedDuty)
            Assert.Equal(CriterionEvidenceState.Pending, Assert.Single(goal.CriterionEvidenceObligations).State);
    }

    internal sealed class Fixture : IDisposable
    {
        internal const string AnswerText = "Observed the landed behavior through the operator intent path.";
        private readonly string _root;
        private readonly OrchestratorWorkspace _workspace;
        private readonly SqliteOrchestratorStateRepository _repository;
        internal Goal Goal { get; }
        internal HumanInputRequest[] Waits { get; }
        internal SqliteOperatorIntentStore Store { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();

        private Fixture(string root, OrchestratorWorkspace workspace,
            SqliteOrchestratorStateRepository repository, Goal goal, HumanInputRequest[] waits)
        {
            _root = root;
            _workspace = workspace;
            _repository = repository;
            Goal = goal;
            Waits = waits;
            Store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        }

        internal static async Task<Fixture> Create(int waitCount = 1,
            string availability = PostLandingOperatorCheck.Availability, bool validatedDuty = false, bool closeWait = false)
        {
            var root = CreateTempDirectory("mcg-completed-wait-intent");
            try
            {
                var workspace = OrchestratorWorkspace.ForDirectory(root);
                StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
                var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
                var seed = new AgentOrchestratorKernel();
                var goal = CreateVerifiedSimpleGoal(seed, "Answer a deferred wait after completing the goal");
                if (validatedDuty)
                {
                    const string criterion = "Operator observes the landed behavior.";
                    seed.SetGoalRefinedSpec(goal.Id, new RefinedSpec(criterion, [criterion],
                        VerificationClass.TestVerifiable, [], [])
                    {
                        OperatorOwnedAcceptanceCriteria = [criterion],
                        CriterionEvidenceDeclarations = [new(criterion, CriterionEvidencePhase.PostLanding,
                            new(CriterionObservationKind.Runtime, "Organic operation uses the change", "local-runtime"), Atomic: true)]
                    });
                    Assert.True(Assert.Single(goal.CriterionEvidenceObligations).IsValidatedPostLanding);
                }
                for (var index = 0; index < waitCount; index++)
                    PostLandingOperatorCheckTests.Request(seed, goal, availability, needed: $"Observe independent behavior {index}");
                var waits = seed.HumanInputRequests.ToArray();
                if (closeWait) seed.SubmitHumanInput(waits[0].Id, "Earlier observation");
                seed.CompleteGoal(goal.Id, "Implementation landed; operator observations remain answerable.");
                Assert.Equal(GoalStatus.Completed, goal.Status);
                await repository.SaveAsync(seed);
                return new(root, workspace, repository, goal, waits);
            }
            catch { TryDeleteDirectory(root); throw; }
        }

        internal OperatorIntentRecord Answer(string targetId) => Intent(OperatorIntentVerbs.Answer,
            new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, targetId,
                Goal.Id.Value, AnswerText, OperatorActorKind.Human));

        internal OperatorIntentRecord Intent(string verb, object payload) => new(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), verb, Goal.Id.Value, null,
            JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
            "operator", "cli", "local-process", DateTimeOffset.UtcNow);

        internal Task<AgentOrchestratorKernel> Reload() => _repository.LoadGoalsAsync([Goal.Id]);

        internal BatchLoopSummary Run(ConductorDriver driver, int iterations = 1,
            Action<AgentOrchestratorKernel>? onLoad = null, Action<AgentOrchestratorKernel>? onPersist = null,
            Action<BatchTickSummary>? onTick = null)
        {
            Assert.Empty(Kernel.Goals);
            var observations = new Dictionary<string, ConductorGoalReloadObservation>();
            var context = new CliExecutionContext(Kernel, _workspace, new InMemoryModelProviderRegistry([]),
                DefaultAgents(), WorkerProfileCatalog.Default(), currentGoal: null,
                loadOperatorIntentTargets: ids =>
                {
                    Assert.Equal(Goal.Id, Assert.Single(ids));
                    var loaded = _repository.LoadGoalsAsync(ids).GetAwaiter().GetResult();
                    onLoad?.Invoke(loaded);
                    return loaded;
                });
            var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
            return new ConductorBatchLoop(operatorIntents: new OperatorIntentCoordinator(Store),
                prepareOperatorIntentTarget: (current, id) => OperatorIntentTargetPreparation.PrepareOperatorIntentTarget(
                    context, current, id, runner, observations),
                goalReloadObservation: id => observations.GetValueOrDefault(id) ?? new ConductorGoalReloadObservation.NotObserved())
                .Run(Kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(_root, "stop"),
                    // Application and durable confirmation each consume one tick; no daemon idle polling.
                    maxIterations: iterations,
                    onTick: onTick,
                    sleepFunc: _ => throw new InvalidOperationException("Completed-goal intent test entered an idle wait."),
                    persistGoalTick: (current, ids) =>
                    {
                        Assert.Contains(Goal.Id, ids);
                        onPersist?.Invoke(current);
                        // Save the waits and timeline together so the next preparation reload sees the answer.
                        _repository.SaveAsync(current).GetAwaiter().GetResult();
                    });
        }

        public void Dispose() => TryDeleteDirectory(_root);
    }
}
