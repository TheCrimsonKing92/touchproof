using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe integration test: a unique root owns the worktree, store and cleanup state.
public sealed class RolePackageRetentionSweepSurvivalTests
{
    [Xunit.Fact]
    public void DurableCopySurvivesSweepAndWorktreeRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-retention-sweep", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, GoalWorktreeLayout.DirectoryName, "fixture");
        var storeRoot = Path.Combine(root, ".orchestrator");
        Directory.CreateDirectory(worktree);
        try
        {
            var task = new TaskSpec(TaskId.New(), "Plan the retained package.", AgentRole.Planner);
            var goal = new AgentOrchestratorKernel().CreateGoal("Survive workspace cleanup", [task]);
            var context = WorkerContextArtifacts.Write(goal, task, worktree, orchestratorStoreRoot: storeRoot);
            var durable = RolePackageRetention.TaskDirectory(storeRoot, goal.Id, task.Id);
            var metadataPath = Path.Combine(durable, "context-package.json");
            Assert.True(Directory.Exists(context));
            Assert.True(File.Exists(metadataPath));
            var originalBytes = File.ReadAllBytes(metadataPath);
            var deletions = new List<string>();
            var hooks = new GoalWorktreeCleanupHooks
            {
                DeleteDirectoryForCleanup = path =>
                {
                    Directory.Delete(path, recursive: true);
                    deletions.Add(path);
                    return GoalWorktreeDeleteResult.Success;
                },
                FindLockHoldersForCleanup = _ => [],
                CleanupWarningSink = warning => throw new InvalidOperationException(warning.ToString())
            };

            var sweep = GoalWorktrees.SweepOwnedEphemeralDirectories(worktree, goal.Id, hooks: hooks);

            Assert.Empty(sweep.LeftoverPaths);
            Assert.Contains(context, deletions);
            Assert.False(Directory.Exists(context));
            Directory.Delete(worktree, recursive: true);
            Assert.False(Directory.Exists(worktree));
            Assert.True(Directory.Exists(durable));
            Assert.True(File.Exists(metadataPath));
            Assert.Equal(originalBytes, File.ReadAllBytes(metadataPath));
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
            Assert.Equal(goal.Id.Value, metadata.RootElement.GetProperty("goalId").GetString());
            Assert.Equal(task.Id.Value, metadata.RootElement.GetProperty("taskId").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
