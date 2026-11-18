using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: each test owns its stores; launch and clock overrides are scoped by AsyncLocal.
public sealed class GoalRefinementStaleSpecHydrationTests
{
    private const string Criterion = "The selected API version is explicit.";
    private const string Answer = "Use v2.";

    [Fact]
    public async Task SyncedStoredSpecHydratesStaleInMemorySpecWithoutRepair()
    {
        var scenario = await CreateScenarioAsync();
        var stale = scenario.Kernel.GetGoal(scenario.GoalId);
        await AssertResolvedAnswerNeedsRepairAsync(scenario);
        await scenario.Repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.GoalId));

        var result = await GoalRefinementWorkCoordinator.ProcessAsync(
            scenario.Repository, scenario.Workspace, scenario.Providers,
            WorkerProfileCatalog.Default(), scenario.GoalId);

        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        var stored = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        Assert.Equal("Answered", Assert.Single(stored.RefinedSpec!.OpenQuestions).Status);
        Assert.Equal(Answer, Assert.Single(stored.RefinedSpec.OpenQuestions).Answer);
        Assert.Equal("Open", Assert.Single(stale.RefinedSpec!.OpenQuestions).Status);
        Assert.Equal(stale.AuthoritativeRefinedSpecVersion!.Version, stored.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Null(await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.GoalId)));
        await AssertResolvedAnswerNeedsRepairAsync(scenario);
        // Make an attempted insert/acquisition observable even if a later path removes the row.
        await ForbidOutboxWritesAsync(scenario);
        using var launch = new LaunchScope(scenario);

        GoalDispatchOperations.EnsureRefinedForSpecConsumer(
            scenario.Kernel, scenario.Workspace, scenario.Providers, stale);

        var hydrated = scenario.Kernel.GetGoal(scenario.GoalId);
        Assert.NotSame(stale, hydrated);
        Assert.Equal("Answered", Assert.Single(hydrated.RefinedSpec!.OpenQuestions).Status);
        Assert.Equal(Answer, Assert.Single(hydrated.RefinedSpec.OpenQuestions).Answer);
        Assert.Equal(GoalRefinementReadiness.Ready, GoalRefinementGate.Evaluate(hydrated, null,
            new(await scenario.Collaboration.ListAsync(scenario.GoalId.Value))).Readiness);
        Assert.Null(await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.GoalId)));
        Assert.Empty(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        Assert.DoesNotContain(stale.Timeline, entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
        Assert.DoesNotContain(hydrated.Timeline, entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
        Assert.DoesNotContain((await scenario.Repository.LoadGoalAsync(scenario.GoalId))!.Timeline,
            entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
        Assert.Equal(launch.PreviousAttempt, launch.Attempts.Get(scenario.GoalId));
        Assert.Equal(0, launch.Count);
    }

    [Fact]
    public async Task UnsyncedStoredSpecStillRepairsAndLaunchesWithinCadence()
    {
        var scenario = await CreateScenarioAsync();
        var goal = scenario.Kernel.GetGoal(scenario.GoalId);
        await AssertResolvedAnswerNeedsRepairAsync(scenario);
        var stored = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        Assert.Equal("Open", Assert.Single(stored.RefinedSpec!.OpenQuestions).Status);
        Assert.Null(Assert.Single(stored.RefinedSpec.OpenQuestions).Answer);
        Assert.Null(await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.GoalId)));
        using var launch = new LaunchScope(scenario);

        var pending = Assert.Throws<InvalidOperationException>(() =>
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(
                scenario.Kernel, scenario.Workspace, scenario.Providers, goal));

        Assert.StartsWith("SPEC_REFINEMENT_PENDING", pending.Message, StringComparison.Ordinal);
        Assert.Contains("state=repair=acquired", pending.Message, StringComparison.Ordinal);
        Assert.Contains("repaired=true", pending.Message, StringComparison.Ordinal);
        Assert.Contains("executor_started=true", pending.Message, StringComparison.Ordinal);
        Assert.Same(goal, scenario.Kernel.GetGoal(scenario.GoalId));
        Assert.Equal(1, launch.Count);
        Assert.Equal(LaunchScope.Now, launch.Attempts.Get(scenario.GoalId)!.LastLaunchAt);
        Assert.Equal(GoalRefinementWorkCoordinator.MessageId(scenario.GoalId),
            Assert.Single(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind)).Id);
        Assert.Contains(goal.Timeline, entry => entry.Message == GoalRefinementWorkCoordinator.PendingPolicyReceipt);
    }

    [Fact]
    public async Task PendingOutboxWithEqualStoredSpecStillDefersByCadence()
    {
        var scenario = await CreateScenarioAsync();
        var goal = scenario.Kernel.GetGoal(scenario.GoalId);
        await scenario.Repository.EnsureOutboxMessageAsync(
            GoalRefinementWorkCoordinator.CreateMessage(scenario.GoalId));
        var outbox = await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.GoalId));
        Assert.Equal(OrchestratorStateOutboxStatus.Pending, outbox!.Status);
        using var launch = new LaunchScope(scenario);

        var pending = Assert.Throws<InvalidOperationException>(() =>
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(
                scenario.Kernel, scenario.Workspace, scenario.Providers, goal));

        Assert.StartsWith("SPEC_REFINEMENT_PENDING", pending.Message, StringComparison.Ordinal);
        Assert.Contains("state=pending", pending.Message, StringComparison.Ordinal);
        Assert.Contains("repaired=false", pending.Message, StringComparison.Ordinal);
        Assert.Contains("executor_started=false", pending.Message, StringComparison.Ordinal);
        Assert.Contains("detail=executor-launch-deferred-cadence", pending.Message, StringComparison.Ordinal);
        Assert.Same(goal, scenario.Kernel.GetGoal(scenario.GoalId));
        Assert.Equal(0, launch.Count);
        Assert.Equal(launch.PreviousAttempt, launch.Attempts.Get(scenario.GoalId));
        Assert.Equal(outbox, await scenario.Repository.GetOutboxStateAsync(
            GoalRefinementWorkCoordinator.MessageId(scenario.GoalId)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StoredVersionProgressionHydratesButUnrelatedDifferencesDoNot(bool higherVersion, bool changeContract)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Use the current refined spec.");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Original contract.", [Criterion],
            VerificationClass.TestVerifiable, [], []));
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var version = Assert.Single(snapshot.RefinedSpecVersions!);
        var storedSpec = version.Spec with
        {
            BehavioralContract = changeContract ? "Unrelated contract change." : version.Spec.BehavioralContract
        };
        var storedKernel = new AgentOrchestratorKernel();
        storedKernel.ReplaceGoalWithSnapshot(snapshot with
        {
            RefinedSpec = storedSpec,
            RefinedSpecVersions = higherVersion
                ? [version with { SupersededByVersion = 2 }, version with { Version = 2, Spec = storedSpec }]
                : [version with { Spec = storedSpec }]
        });
        await repository.SaveAsync(storedKernel);

        GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel, workspace, new InMemoryModelProviderRegistry([]), goal);

        var current = kernel.GetGoal(goal.Id);
        if (higherVersion)
            Assert.NotSame(goal, current);
        else
            Assert.Same(goal, current);
        Assert.Equal(higherVersion ? 2 : 1, current.AuthoritativeRefinedSpecVersion!.Version);
        Assert.Equal("Original contract.", current.RefinedSpec!.BehavioralContract);
        Assert.Empty(await repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
    }

    [Fact]
    public async Task AbsentStoredGoalLeavesInMemoryGoalUntouched()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep the in-memory spec when durable state is absent.");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Keep this contract.", [Criterion],
            VerificationClass.TestVerifiable, [], []));
        Assert.Null(await repository.LoadGoalAsync(goal.Id));

        GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel, workspace, new InMemoryModelProviderRegistry([]), goal);

        Assert.Same(goal, kernel.GetGoal(goal.Id));
    }

    private static async Task AssertResolvedAnswerNeedsRepairAsync(Scenario scenario)
    {
        var readiness = GoalRefinementGate.Evaluate(scenario.Kernel.GetGoal(scenario.GoalId), null,
            new(await scenario.Collaboration.ListAsync(scenario.GoalId.Value)));
        Assert.Equal(GoalRefinementReadiness.RepairRequired, readiness.Readiness);
        Assert.Equal("resolved-answers-pending-sync", readiness.Detail);
    }

    private static async Task ForbidOutboxWritesAsync(Scenario scenario)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scenario.Workspace.SqliteStatePath
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER forbid_refinement_insert BEFORE INSERT ON state_outbox
            BEGIN SELECT RAISE(ABORT, 'unexpected refinement outbox insert'); END;
            CREATE TRIGGER forbid_refinement_update BEFORE UPDATE ON state_outbox
            BEGIN SELECT RAISE(ABORT, 'unexpected refinement outbox acquisition'); END;
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ('forbid_refinement_insert', 'forbid_refinement_update')";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
    }

    private static async Task<Scenario> CreateScenarioAsync()
    {
        var workspace = OrchestratorWorkspace.ForDirectory(CreateTempDirectory());
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal($"Select the API version.\n\n## Acceptance criteria\n\n1. {Criterion}");
        var questionId = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:api-version";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Use the selected API version.", [Criterion],
            VerificationClass.TestVerifiable, [],
            [new RefinedSpecOpenQuestion(questionId, "Which API version should be used?",
                "external-contract", "Open", TopicKey: "api-version")]));
        await repository.SaveAsync(kernel);
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        await collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
            "API version", "Choose the API version.", questionId);
        Assert.True(await collaboration.TryResolveAsync(questionId, Answer));
        return new(workspace, repository, kernel, goal.Id, collaboration, new InMemoryModelProviderRegistry([]));
    }

    private sealed class LaunchScope : IDisposable
    {
        internal static readonly DateTimeOffset Now = new(2026, 11, 17, 19, 0, 0, TimeSpan.Zero);
        private readonly IDisposable _clock;
        private readonly Func<OrchestratorWorkspace, GoalId, GoalRefinementWorkLaunchResult>? _previousOverride;
        internal SpecRefinementLaunchAttemptStore Attempts { get; }
        internal SpecRefinementLaunchAttempt PreviousAttempt { get; }
        internal int Count { get; private set; }

        internal LaunchScope(Scenario scenario)
        {
            _clock = GoalRefinementWorkCoordinator.UseClock(() => Now);
            Attempts = SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace);
            PreviousAttempt = new(Now.AddMinutes(-1), 0, null,
                GoalRefinementWorkCoordinator.MessageId(scenario.GoalId), scenario.Workspace.SqliteStatePath);
            Attempts.Save(scenario.GoalId, PreviousAttempt);
            _previousOverride = GoalRefinementWorkCoordinator.LaunchOverride;
            GoalRefinementWorkCoordinator.LaunchOverride = (workspace, goalId) =>
            {
                Assert.Same(scenario.Workspace, workspace);
                Assert.Equal(scenario.GoalId, goalId);
                Count++;
                return new GoalRefinementWorkLaunchResult(true, 1, "test-launch");
            };
        }

        public void Dispose()
        {
            GoalRefinementWorkCoordinator.LaunchOverride = _previousOverride;
            _clock.Dispose();
        }
    }

    private sealed record Scenario(
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        AgentOrchestratorKernel Kernel,
        GoalId GoalId,
        CollaborationItemStore Collaboration,
        IModelProviderRegistry Providers);
}
