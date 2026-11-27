using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class GoalExecutionProjectionTests
{
    [Fact]
    public void Build_MissingWorkspace_ExposesGoalFactsVerdictAndAcceptance()
    {
        var (kernel, _, goal, agents) = ReadinessTestSeed.Create();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var resolutions = 0;

        var projection = GoalExecutionProjection.Build(goal, agents, root, WorkerProfileCatalog.Default(),
            kernel.ProviderHoldScope, ReadinessTestSeed.Now, (directory, goalId) =>
            {
                Assert.Equal(root, directory);
                Assert.Equal(goal.Id, goalId);
                resolutions++;
                return null;
            }, kernel);

        Assert.Equal(1, resolutions);
        Assert.Same(goal, projection.Goal);
        Assert.False(projection.LifecycleFacts.WorkspaceExists);
        Assert.Null(projection.WorkspacePath);
        Assert.IsType<DispatchReadinessReady>(projection.Verdict);
        var acceptance = Assert.IsType<GoalAcceptanceSummary>(projection.Acceptance);
        Assert.Equal(goal.Id, acceptance.GoalId);
        Assert.Equal(goal.Status, acceptance.Status);
    }

    [Fact]
    public void Build_DefaultResolver_LifecycleFactsMatchReader()
    {
        var (kernel, _, goal, agents) = ReadinessTestSeed.Create();
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(directory.FullName);
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, "conductor:land", "Landed.");
            GoalOperationJournal.Failed(workspace.ExecutionDirectory, goal, "conductor:cleanup", "Cleanup failed.");

            var projection = GoalExecutionProjection.Build(goal, agents, workspace.ExecutionDirectory,
                WorkerProfileCatalog.Default(), kernel.ProviderHoldScope, ReadinessTestSeed.Now, kernel: kernel);

            Assert.Equal(GoalLifecycleFactsReader.Read(workspace, goal, hasOpenClarification: false),
                projection.LifecycleFacts);
            Assert.True(projection.LifecycleFacts.IsMerged);
            Assert.True(projection.LifecycleFacts.IsBlocked);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
