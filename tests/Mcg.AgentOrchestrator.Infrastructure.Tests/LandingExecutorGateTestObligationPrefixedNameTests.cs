using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories and attempt files are fixture-owned; console capture is flow-local.
public sealed class LandingExecutorGateTestObligationPrefixedNameTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    [Fact]
    public void UniqueAddedClass_PrintsBindingWithoutObligationHold()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, originalKernel, originalGoal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/change.txt");
            var worktree = GoalWorktrees.Ensure(repo, originalGoal.Id);
            const string path = "tests/Fixture/FamilyPlannedTests.cs";
            TestClassRenameEvidenceTests.Write(worktree, path, TestClassRenameEvidenceTests.Source("FamilyPlannedTests"));
            TestClassRenameEvidenceTests.Commit(worktree, "Add prefixed class", path);
            var candidate = LandingExecutorTests.ReadGit(worktree, "rev-parse", "HEAD");
            var kernel = GateTestObligationIntakeTests.Seed(originalKernel, originalGoal, ["PlannedTests"]);
            var goal = kernel.GetGoal(originalGoal.Id);
            var (source, _, _) = ProspectiveEvidenceGateFixture.WriteAttempt(workspace.OrchestratorDirectory,
                goal, candidate, [("FamilyPlannedTests.Check", "Passed")]);
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound,
                Assert.Single(GateTestObligationEvaluator.Evaluate(goal, workspace.OrchestratorDirectory, candidate)).Outcome);

            var output = AsyncLocalConsoleRouter.Capture(() =>
            {
                var result = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, candidate);
                Assert.Empty(result.ObligationLines);
            });

            var line = Assert.Single(output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(item => item.StartsWith("GATE_TEST_OBLIGATION_RENAME_FOLLOWED", StringComparison.Ordinal)));
            Assert.Equal($"GATE_TEST_OBLIGATION_RENAME_FOLLOWED id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
                $"old=PlannedTests new=FamilyPlannedTests old_path=none new_path={path} similarity=0 " +
                $"evidence=prefixed-name planned=PlannedTests committed=FamilyPlannedTests candidate={candidate} source={source}", line);
        }
        finally { LandingExecutorTests.TryDeleteDirectory(repo); }
    }
}
