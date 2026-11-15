using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all refs, SQLite receipts and artifacts live in this test's own repository.
public sealed class AcceptanceCohortWorkflowTestsMergeTrainManifestEscape : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void ManifestErrorOutsideFallbackFilter_EscapesRunMergeTrain()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First manifest-escape train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second manifest-escape train member", repo);
            var thirdGoal = CreateCompletedGoal(kernel, "Third manifest-escape train member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/ManifestTrainFirst.cs",
                "first");
            _ = CreateWorktreeCandidate(
                repo,
                secondGoal.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ManifestTrainSecond.cs",
                "second");
            _ = CreateWorktreeCandidate(
                repo,
                thirdGoal.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/ManifestTrainThird.cs",
                "third");
            var manifestException = new NotSupportedException("Unsupported manifest identity during train materialization");
            var verifier = new ManifestThrowingAcceptanceVerifier(manifestException);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(
                kernel, workspace, verifier, AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                cleanupHooks: CreateIsolatedCleanupContext(workspace.ExecutionDirectory).Hooks);
            var selection = ProjectTrainSelection(driver, firstGoal, secondGoal, thirdGoal);

            var escaped = Assert.Throws<NotSupportedException>(() => driver.RunMergeTrain(
                selection,
                [firstGoal, secondGoal, thirdGoal],
                ConductorAutonomyPolicy.Permissive));

            Assert.Same(manifestException, escaped);
            Assert.Equal(0, verifier.RunCount);
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
