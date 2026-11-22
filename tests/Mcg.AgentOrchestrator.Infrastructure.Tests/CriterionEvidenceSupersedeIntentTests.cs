using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each scenario owns its SQLite databases and console capture is async-local.
public sealed class CriterionEvidenceSupersedeIntentTests
{
    private const string Reason = "The follow-up work already resolved the original duty.";
    private const string Actor = "miles";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);

    [Fact]
    public async Task QueuedDuty_AppliesCapturedReasonAndLeavesAttentionAfterReload()
    {
        using var scenario = new Scenario();
        await scenario.Repository.SaveAsync(scenario.Kernel);
        var before = await scenario.Repository.ReadAttentionProjectionAsync();
        Assert.Equal(scenario.Obligation.Id, Assert.Single(before!.PostLandingDuties!).Obligation.Id);
        Assert.Contains(scenario.Obligation.Id, AsyncLocalConsoleRouter.Capture(() =>
            PostLandingDutiesTextView.Print([scenario.Goal])), StringComparison.Ordinal);

        File.WriteAllText(scenario.ReasonPath, $"  {Reason}\r\n");
        AsyncLocalConsoleRouter.Capture(() => scenario.Submit());
        var intent = Assert.Single(await scenario.Store.ListForGoalAsync(scenario.Goal.Id.Value));
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(CriterionEvidenceState.Pending, scenario.Obligation.State);
        Assert.Empty(intent.PayloadFileReferences);
        File.Delete(scenario.ReasonPath);

        var coordinator = new OperatorIntentCoordinator(scenario.Store);
        Assert.True(coordinator.ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        await scenario.Repository.SaveAsync(scenario.Kernel);
        coordinator.CompletePersisted([scenario.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await scenario.Store.GetAsync(intent.Id))!.Status);
        AssertRetired(scenario.Goal, scenario.Obligation.Id);
        Assert.DoesNotContain(scenario.Obligation.Id, AsyncLocalConsoleRouter.Capture(() =>
            PostLandingDutiesTextView.Print([scenario.Goal])), StringComparison.Ordinal);

        var snapshot = await scenario.Repository.LoadGoalAsync(scenario.Goal.Id);
        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot!], [])).GetGoal(scenario.Goal.Id);
        AssertRetired(restored, scenario.Obligation.Id);
        var after = await scenario.Repository.ReadAttentionProjectionAsync();
        Assert.Empty(after!.PostLandingDuties!);
    }

    [Theory]
    [InlineData(GoalStatus.Completed, "pre", Reason, "must be PostLanding")]
    [InlineData(GoalStatus.Completed, "satisfied", Reason, "must be Pending")]
    [InlineData(GoalStatus.Completed, "acceptance", Reason, "must be Operator-owned")]
    [InlineData(GoalStatus.Active, "post", Reason, "must be Completed or CleanedUp")]
    [InlineData(GoalStatus.Verified, "post", Reason, "must be Completed or CleanedUp")]
    [InlineData(GoalStatus.Completed, "post", " \r\n ", "nonempty supersede reason")]
    public async Task IneligibleIntent_NamesRuleAndLeavesGoalUnchanged(
        GoalStatus status, string target, string reason, string rule)
    {
        using var scenario = new Scenario(status, target);
        var before = JsonSerializer.Serialize(scenario.Kernel.ExportGoalSnapshot(scenario.Goal.Id));
        var intent = await scenario.Enqueue(reason);
        Assert.False(new OperatorIntentCoordinator(scenario.Store).ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        var after = (await scenario.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, after.Status);
        Assert.Contains(rule, after.Outcome, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(scenario.Kernel.ExportGoalSnapshot(scenario.Goal.Id)));
    }

    [Fact]
    public async Task CleanedUpVerifiedGoal_AllowsSupersession()
    {
        using var scenario = new Scenario(GoalStatus.Verified);
        var directory = scenario.Workspace.ExecutionDirectory;
        GoalOperationJournal.Completed(directory, scenario.Goal, "conductor:land", "Landed.");
        GoalOperationJournal.Completed(directory, scenario.Goal, "conductor:record", "Recorded.");
        GoalOperationJournal.RecordCleanupCompleted(directory, scenario.Goal, "Cleaned up.");
        var coordinator = OperatorIntentCoordinator.CreateDefault(scenario.Workspace);
        var intent = await scenario.Enqueue(Reason);

        Assert.True(coordinator.ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        coordinator.CompletePersisted([scenario.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await scenario.Store.GetAsync(intent.Id))!.Status);
        Assert.Equal(GoalStatus.Verified, scenario.Goal.Status);
        Assert.Equal(CriterionEvidenceState.Superseded, scenario.Obligation.State);
    }

    [Fact]
    public async Task AlreadySupersededIntent_RefusesAndPreservesFirstAttribution()
    {
        using var scenario = new Scenario();
        var coordinator = new OperatorIntentCoordinator(scenario.Store);
        await scenario.Enqueue(Reason);
        Assert.True(coordinator.ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        coordinator.CompletePersisted([scenario.Goal.Id]);
        var before = scenario.Obligation;

        var retry = await scenario.Enqueue("A different reason.", "another operator");
        Assert.False(coordinator.ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        var rejected = (await scenario.Store.GetAsync(retry.Id))!;
        Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
        Assert.Contains("must be Pending", rejected.Outcome, StringComparison.Ordinal);
        Assert.Equal(before, scenario.Obligation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public async Task EmptyReasonFile_RefusesBeforeQueueing(string contents)
    {
        using var scenario = new Scenario();
        await scenario.Repository.SaveAsync(scenario.Kernel);
        File.WriteAllText(scenario.ReasonPath, contents);

        var error = Assert.Throws<ArgumentException>(() => scenario.Submit());
        Assert.Contains("nonempty supersede reason", error.Message, StringComparison.Ordinal);
        Assert.Empty(await scenario.Store.ListForGoalAsync(scenario.Goal.Id.Value));
    }

    [Fact]
    public async Task GoalStatus_PrintsSupersededReasonAlongsidePendingSibling()
    {
        using var scenario = new Scenario(sibling: true);
        var intent = await scenario.Enqueue(Reason);
        var coordinator = new OperatorIntentCoordinator(scenario.Store);
        Assert.True(coordinator.ExecutePending(scenario.Kernel, scenario.Goal).MutatedGoalState);
        coordinator.CompletePersisted([scenario.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await scenario.Store.GetAsync(intent.Id))!.Status);

        var status = AsyncLocalConsoleRouter.Capture(() => GoalTextView.PrintGoal(scenario.Goal));
        Assert.Contains($"{scenario.Obligation.Id}: owner=operator state=Superseded reason={Reason}", status, StringComparison.Ordinal);
        Assert.Contains("criterion-v1-1: owner=operator state=pending", status, StringComparison.Ordinal);
    }

    private static void AssertRetired(Goal goal, string id)
    {
        var retired = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(id, retired.Id);
        Assert.Equal(GoalStatus.Completed, goal.Status);
        Assert.Equal(CriterionEvidenceState.Superseded, retired.State);
        Assert.Equal(Reason, retired.Detail);
        Assert.Equal(Actor, retired.ObservationActor);
        Assert.Equal(At, retired.RecordedAt);
        Assert.Null(retired.ReceiptId);
        Assert.Null(retired.Observation);
        Assert.Empty(goal.OutstandingPostLandingDuties);
    }

    private sealed class Scenario : IDisposable
    {
        private const string Criterion = "Observe the landed behavior";
        private const string Sibling = "Observe the independent duty";
        private static readonly CriterionObservationScope Scope =
            new(CriterionObservationKind.RepositoryState, "A follow-up closes", "backlog item");
        private readonly string _root = Directory.CreateTempSubdirectory("supersede-intent-").FullName;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public OrchestratorWorkspace Workspace { get; }
        public SqliteOrchestratorStateRepository Repository { get; }
        public SqliteOperatorIntentStore Store { get; }
        public string ReasonPath => Path.Combine(_root, "supersession reason.txt");
        public CriterionEvidenceObligation Obligation => Goal.CriterionEvidenceObligations[0];

        public Scenario(GoalStatus status = GoalStatus.Completed, string target = "post", bool sibling = false)
        {
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            Repository = new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            var seed = new AgentOrchestratorKernel(new FixedClock());
            var goal = seed.CreateGoal("Retire a duty");
            seed.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe", sibling ? [Criterion, Sibling] : [Criterion],
                VerificationClass.RealWorldDependent, [], [])
            {
                OperatorOwnedAcceptanceCriteria = target == "acceptance" ? [] : sibling ? [Criterion, Sibling] : [Criterion],
                AcceptanceGateOwnedAcceptanceCriteria = target == "acceptance" ? [Criterion] : [],
                CriterionEvidenceDeclarations = target != "post" ? [] : sibling
                    ? [new(Criterion, CriterionEvidencePhase.PostLanding, Scope, Atomic: true),
                       new(Sibling, CriterionEvidencePhase.PostLanding, Scope, Atomic: true)]
                    : [new(Criterion, CriterionEvidencePhase.PostLanding, Scope, Atomic: true)]
            });
            if (target == "satisfied")
            {
                var obligation = Assert.Single(goal.CriterionEvidenceObligations);
                seed.RecordCriterionEvidence(goal.Id, obligation.Id, CriterionEvidenceOwner.Operator,
                    "candidate", "receipt", obligation.RequiredScope, true, "Observed");
            }

            Kernel = AgentOrchestratorKernel.FromSnapshot(
                new OrchestratorSnapshot([seed.ExportGoalSnapshot(goal.Id) with { Status = status }], []), new FixedClock());
            Goal = Kernel.GetGoal(goal.Id);
        }

        public bool Submit()
        {
            var args = CliArgumentParser.NormalizeArgs(["criterion-evidence-supersede", "--goal", Goal.Id.Value[..8],
                Obligation.Id, "--reason-file", ReasonPath, "--operator-actor", Actor]);
            CliCommandHelp.ThrowIfInvalidFlags(args);
            IReadOnlyList<AgentDefinition> agents = [];
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = null;
            return CliPersistentStateRunner.ExecuteCommand(args, Repository, Workspace, ref agents,
                new InMemoryModelProviderRegistry([]), ref profiles, ref current);
        }

        public Task<OperatorIntentRecord> Enqueue(string reason, string actor = Actor)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.CriterionEvidenceSupersede,
                Goal.Id.Value, null, JsonSerializer.Serialize(
                    new CriterionEvidenceSupersedeOperatorIntentPayload(Obligation.Id, reason), OperatorIntentJson.Options),
                [], actor, "cli", "local-process", At));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => At;
    }
}
