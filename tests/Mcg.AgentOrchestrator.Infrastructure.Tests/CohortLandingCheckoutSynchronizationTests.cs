using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static LandingExecutorCheckoutSynchronizationTests;

// Parallel-safe: each case owns its repositories, stores, and event writer.
public sealed class CohortLandingCheckoutSynchronizationTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Cohort_PostAdvanceBookkeeping_LeavesCheckoutAtMain(bool failFact) =>
        CheckLanding(train: false, failFact);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Train_PostAdvanceBookkeeping_LeavesCheckoutAtMain(bool failFact) =>
        CheckLanding(train: true, failFact);

    private static async Task CheckLanding(bool train, bool failFact)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var goals = new[] { CreateCompletedGoal(kernel, "First synchronization member", repo),
                CreateCompletedGoal(kernel, "Second synchronization member", repo) };
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var candidates = new[]
            {
                CreateCandidate(repo, goals[0].Id.Value, "src/First.cs", "first"),
                CreateCandidate(repo, goals[1].Id.Value, "src/Second.cs", "second")
            };
            goals = goals.Select(goal => AddMismatchedDuty(kernel, goal)).ToArray();
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            if (failFact) ForceFactFailure(workspace.SqliteStatePath);
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            RunGit(repo, "branch", LandingExecutor.IntegrationBranchName, main);
            string commit;
            if (train)
            {
                var bindings = new[]
                {
                    TrainBind(candidates[0].GoalId, candidates[0].Revision, "src/First.cs", "resource:first"),
                    TrainBind(candidates[1].GoalId, candidates[1].Revision, "src/Second.cs", "resource:second")
                };
                using var integration = GoalWorktrees.CreateMergeTrainWorkspace(repo, main, bindings, TrunkBranchName.Default);
                commit = integration.CommitRevision;
                var identity = MergeTrainIdentity.Create(integration.Members, main, integration.TreeRevision, "manifest-v1");
                var store = new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                var receipt = store.SaveGateReceipt(new MergeTrainReceipt("sync-train", identity,
                    MergeTrainGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "sync-train.trx")], ValidForLanding: true));
                Verify(() => LandingExecutor.ExecuteMergeTrain(kernel, goals, workspace, receipt, commit,
                    store, ConductorAutonomyPolicy.Permissive, eventWriter: writer));
            }
            else
            {
                var bindings = new[]
                {
                    Bind(candidates[0].GoalId, candidates[0].Revision, "src/First.cs", "resource:first"),
                    Bind(candidates[1].GoalId, candidates[1].Revision, "src/Second.cs", "resource:second")
                };
                using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(repo, main, bindings, TrunkBranchName.Default);
                commit = integration.CommitRevision;
                var identity = AcceptanceCohortIdentity.Create(bindings, main, integration.TreeRevision,
                    GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(integration.Path, ["src/First.cs", "src/Second.cs"]));
                var store = new CohortAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
                var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt("sync-cohort", identity,
                    AcceptanceCohortGateOutcome.Passed, DateTimeOffset.UtcNow, 100, [], GateExitCode: 0,
                    GateTestResultPaths: [WritePassingTrx(repo, "sync-cohort.trx")], ValidForLanding: true));
                Verify(() => LandingExecutor.ExecuteCohort(kernel, goals, workspace, receipt, commit,
                    store, ConductorAutonomyPolicy.Permissive, eventWriter: writer));
            }
            Assert.NotEqual(main, commit);
            Assert.Equal(commit, RunGitOutput(repo, "rev-parse", "main").Trim());
            AssertCheckoutAtMain(repo);
            foreach (var goal in goals)
            {
                var duty = Assert.Single(goal.UnpublishedPostLandingDuties);
                Assert.Null(duty.Publication);
                if (!failFact)
                {
                    using var landed = Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist.ReadLanded(workspace, goal.Id);
                }
            }

            void Verify(Func<AcceptanceCohortLandingResult> land)
            {
                if (failFact)
                {
                    var error = Assert.Throws<SqliteException>(() => land());
                    Assert.Contains("forced fact failure", error.Message, StringComparison.Ordinal);
                }
                else
                {
                    var result = land();
                    Assert.True(result.MainAdvanced, result.Message);
                }
            }
        }
        finally { DeleteDirectory(repo); }
    }
}
