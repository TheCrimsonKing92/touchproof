using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns all repositories, worktrees, logs and its state database.
public sealed class GoalRepositoryWorkerCommitTests : GoalWorktreeTestBase
{
    private const string WorkerOutput = """
        WORKER_RESULT:
        files: seed.txt
        summary: Updated declared repositories
        commands: none
        tests: deferred - GoalRepositoryWorkerCommitTests
        commit: none
        blockers: none
        assigned_scope_complete: true
        model_fit: fixture/static - adequate - repository commit fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    [Fact]
    public void CompletionCommitsDirtyRepositoryAndLeavesCleanRepositoryAlone()
    {
        RunScenario(WorkerOutput, (goal, task, paths, seeds) =>
        {
            Assert.True(task.Status == WorkTaskStatus.Completed, task.LastVerification?.StandardError);
            Assert.Equal(seeds["second"], RunGitOutput(paths["second"], "rev-parse", "HEAD^"));
            Assert.Equal("2", RunGitOutput(paths["second"], "rev-list", "--count", "HEAD"));
            Assert.Equal(GoalRepositoryWorktreeLayout.BranchName(goal.Id, "second"),
                RunGitOutput(paths["second"], "branch", "--show-current"));
            Assert.Equal(string.Empty, RunGitOutput(paths["second"], "status", "--porcelain"));
            Assert.Equal("seedworker edit", File.ReadAllText(Path.Combine(paths["second"], "seed.txt")));
            Assert.Equal(RunGitOutput(paths["default"], "log", "-1", "--format=%s"),
                RunGitOutput(paths["second"], "log", "-1", "--format=%s"));
            var message = RunGitOutput(paths["second"], "log", "-1", "--format=%B");
            Assert.Contains($"Goal: {goal.Id.Value}", message);
            Assert.Contains($"Task-Id: {task.Id.Value}", message);
            Assert.Contains($"Dispatch: {BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, task.LastDispatch!)}", message);
            Assert.Equal(seeds["third"], RunGitOutput(paths["third"], "rev-parse", "HEAD"));
            Assert.Equal(string.Empty, RunGitOutput(paths["third"], "status", "--porcelain"));

            // Record the exact git working directories at the production repository-commit seam.
            var targets = GoalRepositoryWorkerPreparation.ResolveTargets(goal, task.RequiredRole);
            var commands = new List<(string Directory, string Operation)>();
            var committer = new DispatchWorktreeCommitter((directory, arguments) =>
            {
                commands.Add((directory, arguments[0]));
                var target = Assert.Single(targets, target => target.WorktreePath == directory);
                return new GitCli.GitResult(0, arguments[0] == "branch"
                    ? GoalRepositoryWorktreeLayout.BranchName(goal.Id, target.Key) : string.Empty, string.Empty);
            });
            Assert.Empty(BackgroundDispatchRunner.CommitRepositoryWorktreeEdits(goal, task, true, WorkerOutput, string.Empty, committer));
            Assert.Equal(targets.Select(target => target.WorktreePath).Order(), commands.Select(command => command.Directory).Distinct().Order());
            Assert.DoesNotContain(commands, command => command.Operation is "add" or "commit");
            foreach (var target in targets)
            {
                Assert.DoesNotContain(commands, command => command.Directory == target.CheckoutRoot);
                Assert.DoesNotContain(commands, command => command.Directory.StartsWith(Path.Combine(target.CheckoutRoot, ".git")));
            }
            commands.Clear();
            var undeclared = new AgentOrchestratorKernel().CreateGoal("Undeclared",
                [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
            Assert.Empty(BackgroundDispatchRunner.CommitRepositoryWorktreeEdits(undeclared, task, true, WorkerOutput, string.Empty, committer));
            Assert.Empty(BackgroundDispatchRunner.CommitRepositoryWorktreeEdits(goal,
                new TaskSpec(TaskId.New(), "Review", AgentRole.Reviewer), true, WorkerOutput, string.Empty, committer));
            Assert.Empty(commands);
        });
    }

    [Fact]
    public void CompletionRefusesUnverifiedRepositoryEditsUnderHomeGuards()
    {
        RunScenario("Completed implementation.", (_, task, paths, seeds) =>
        {
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Assert.Contains("repository=second", task.LastVerification!.StandardError);
            Assert.Contains("Commit-on-behalf guards refused repository edits.", task.LastVerification.StandardError);
            Assert.Equal(seeds["second"], RunGitOutput(paths["second"], "rev-parse", "HEAD"));
            Assert.Equal("M seed.txt", RunGitOutput(paths["second"], "status", "--porcelain"));
        });
    }

    [Fact]
    public void CompletionRejectsWrongRepositoryBranchWithoutCommitting()
    {
        RunScenario(WorkerOutput, (_, task, paths, seeds) =>
        {
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            Assert.Contains("repository=second; worktree inspection refused; reason=branch-mismatch", task.LastVerification!.StandardError);
            Assert.Equal(seeds["second"], RunGitOutput(paths["second"], "rev-parse", "HEAD"));
            Assert.Equal("M seed.txt", RunGitOutput(paths["second"], "status", "--porcelain"));
        }, beforeRefresh: paths => RunGit(paths["second"], "switch", "-c", "wrong-branch"));
    }

    [Fact]
    public void InspectionCacheKeepsHomeAndRepositoryBranchExpectationsSeparate()
    {
        var goalId = GoalId.New();
        var branch = GoalRepositoryWorktreeLayout.BranchName(goalId, "second");
        var path = Path.Combine(Path.GetTempPath(), "inspection-fixture");
        var inspectedDirectories = new List<string>();
        var committer = new DispatchWorktreeCommitter((directory, arguments) =>
        {
            inspectedDirectories.Add(directory);
            return new GitCli.GitResult(0, arguments[0] == "branch" ? branch : string.Empty, string.Empty);
        }, directoryExists: _ => true, fileExists: _ => true);
        var dispatchedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(committer.InspectGoalWorktree(path, goalId, dispatchedAt, expectedBranch: branch).IsAvailable);
        Assert.True(committer.InspectGoalWorktree(path, goalId, dispatchedAt).IsUnsafe);
        Assert.NotEmpty(inspectedDirectories);
        Assert.All(inspectedDirectories, directory => Assert.Equal(path, directory));
    }

    private static void RunScenario(string output,
        Action<Goal, TaskSpec, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>> assertOutcome,
        Action<IReadOnlyDictionary<string, string>>? beforeRefresh = null)
    {
        var roots = new Dictionary<string, string>();
        try
        {
            foreach (var key in new[] { "default", "second", "third" }) roots[key] = PastDatedSeedRepository.Create();
            var home = roots["default"];
            _ = CreateMigratedStateRepository(OrchestratorWorkspace.ForDirectory(home).SqliteStatePath);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("# Repository edits\nDetails", [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
            kernel.DeclareGoalRepositories(goal.Id, new GoalRepositoryDeclaration(roots.Keys.ToArray(), roots));
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var secondaryRoots = roots.Where(entry => entry.Key != "default").ToDictionary(entry => entry.Key, entry => entry.Value);
            Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(secondaryRoots.Keys),
                goal.Id, secondaryRoots, out var bindings, out var failure), failure);
            var paths = bindings!.ToDictionary(entry => entry.Key, entry => entry.Value.WorktreePath);
            paths["default"] = GoalWorktrees.Ensure(home, goal.Id);
            var seeds = paths.ToDictionary(entry => entry.Key, entry => RunGitOutput(entry.Value, "rev-parse", "HEAD"));
            var checkoutHeads = roots.ToDictionary(entry => entry.Key, entry => RunGitOutput(entry.Value, "rev-parse", "HEAD"));
            var checkoutBranches = roots.ToDictionary(entry => entry.Key, entry => RunGitOutput(entry.Value, "branch", "--show-current"));
            // Provisioned worktrees are untracked at the root. Compare complete root status before/after.
            var checkoutStatuses = roots.ToDictionary(entry => entry.Key, entry => RunGitOutput(entry.Value, "status", "--porcelain"));
            var dispatchedAt = clock.UtcNow.AddMinutes(-1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", paths["default"], dispatchedAt));
            foreach (var key in new[] { "default", "second" }) File.AppendAllText(Path.Combine(paths[key], "seed.txt"), "worker edit");
            var logs = Directory.CreateDirectory(Path.Combine(home, "logs")).FullName;
            var stdout = Path.Combine(logs, "worker.out.log");
            var stderr = Path.Combine(logs, "worker.err.log");
            var exit = Path.Combine(logs, "worker.exit.txt");
            File.WriteAllText(stdout, output);
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt",
                paths["default"], stdout, stderr, exit, dispatchedAt, null, null));
            beforeRefresh?.Invoke(paths);
            new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);
            assertOutcome(goal, task, paths, seeds);
            foreach (var entry in secondaryRoots)
            {
                Assert.Equal(checkoutHeads[entry.Key], RunGitOutput(entry.Value, "rev-parse", "HEAD"));
                Assert.Equal(checkoutBranches[entry.Key], RunGitOutput(entry.Value, "branch", "--show-current"));
                Assert.Equal(checkoutStatuses[entry.Key], RunGitOutput(entry.Value, "status", "--porcelain"));
                Assert.Equal("seed", File.ReadAllText(Path.Combine(entry.Value, "seed.txt")));
            }
        }
        finally
        {
            foreach (var root in roots.Values) PastDatedSeedRepository.Delete(root);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
