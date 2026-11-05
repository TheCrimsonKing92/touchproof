using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: repositories, databases, and event writers are private; no shared git seam changes.
public sealed class LandingExecutorCheckoutSynchronizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_PostAdvanceBookkeeping_LeavesCheckoutAtMain(bool failFact)
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/synchronization.txt");
            goal = AddMismatchedDuty(kernel, goal);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await repository.SaveAsync(kernel);
            if (failFact) ForceFactFailure(workspace.SqliteStatePath);
            var before = LandingExecutorTests.ReadGit(root, "rev-parse", "main");
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);

            if (failFact)
            {
                var error = Assert.Throws<SqliteException>(() => LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer));
                Assert.Contains("forced fact failure", error.Message, StringComparison.Ordinal);
            }
            else
            {
                var result = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);
                Assert.True(result.MainAdvanced, result.Message);
                using var landed = Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist.ReadLanded(workspace, goal.Id);
                var duty = Assert.Single(goal.UnpublishedPostLandingDuties);
                Assert.Contains(duty.Id, result.Message, StringComparison.Ordinal);
            }
            Assert.NotEqual(before, LandingExecutorTests.ReadGit(root, "rev-parse", "main"));
            AssertCheckoutAtMain(root);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    internal static Goal AddMismatchedDuty(AgentOrchestratorKernel kernel, Goal goal)
    {
        const string criterion = "Observe original candidate in the running service";
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective, [criterion],
            VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = [criterion],
            CriterionEvidenceDeclarations = [new(criterion, CriterionEvidencePhase.PostLanding,
                new(CriterionObservationKind.Runtime, "Observe the activated behavior", "runtime"), Atomic: true)]
        });
        // Restore the persisted declaration; new operator mappings cannot rewrite post-landing authority.
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        kernel.ReplaceGoalWithSnapshot(snapshot with
        {
            CriterionEvidenceObligations = snapshot.CriterionEvidenceObligations!.Select(duty =>
                duty with { ExpectedCandidateSha = new string('f', 40) }).ToArray()
        });
        goal = kernel.GetGoal(goal.Id);
        Assert.Single(goal.UnpublishedPostLandingDuties);
        return goal;
    }

    internal static void ForceFactFailure(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_fact BEFORE INSERT ON goal_promotion_facts BEGIN SELECT RAISE(ABORT, 'forced fact failure'); END";
        command.ExecuteNonQuery();
    }

    internal static void AssertCheckoutAtMain(string root)
    {
        Assert.Equal("main", LandingExecutorTests.ReadGit(root, "branch", "--show-current"));
        Assert.Equal(0, GitCli.Run(root, "diff", "--cached", "--quiet", "main", "--").ExitCode);
        Assert.Equal(0, GitCli.Run(root, "diff", "--quiet", "--").ExitCode);
    }
}
