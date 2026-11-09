using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each scenario owns its repositories, worktrees, logs and state database.
public sealed class GoalRepositoryChangeEvidenceTests : GoalWorktreeTestBase
{
    private const string WorkerOutput = """
        WORKER_RESULT:
        files: seed.txt
        commands: none
        tests: deferred - GoalRepositoryChangeEvidenceTests
        commit: none
        blockers: none
        assigned_scope_complete: true
        model_fit: fixture/static - adequate - repository evidence fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclaredRepositoryCommit_CleanHome_Completes(bool commitBeforeRefresh)
    {
        RunScenario(declared: true, scenario =>
        {
            File.AppendAllText(Path.Combine(scenario.Paths["second"], "seed.txt"), "worker edit");
            if (commitBeforeRefresh) Commit(scenario.Paths["second"], "seed.txt");
            Assert.Equal(scenario.Seeds["default"], RunGitOutput(scenario.Paths["default"], "rev-parse", "HEAD"));
            Assert.Empty(RunGitOutput(scenario.Paths["default"], "status", "--porcelain"));
        }, scenario =>
        {
            AssertCompleted(scenario.Task);
            Assert.DoesNotContain(DispatchRejectionDiagnosticMarker.Prefix, scenario.Task.LastVerification!.StandardError);
            Assert.Equal(scenario.Seeds["second"], RunGitOutput(scenario.Paths["second"], "rev-parse", "HEAD^"));
            Assert.Empty(RunGitOutput(scenario.Paths["second"], "status", "--porcelain"));
            Assert.Equal(scenario.Seeds["default"], RunGitOutput(scenario.Paths["default"], "rev-parse", "HEAD"));
        });
    }

    [Fact]
    public void DeclaredGoal_NoCommits_KeepsExistingRejection()
    {
        RunScenario(declared: true, _ => { }, scenario =>
        {
            var diagnostic = AssertRejected(scenario.Task);
            Assert.True(DispatchRejectionDiagnosticMarker.TryParse(diagnostic, out var recognized,
                out var reason, out var commits, out var paths), diagnostic);
            Assert.False(recognized);
            Assert.Equal(DispatchRejectionDiagnosticMarker.VerificationPatternUnmatched, reason);
            Assert.Equal(0, commits);
            Assert.Equal("none", paths);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndeclaredGoal_HomeEvidence_DeterminesCompletion(bool commitInHome)
    {
        RunScenario(declared: false, scenario =>
        {
            if (!commitInHome) return;
            File.AppendAllText(Path.Combine(scenario.Paths["default"], "seed.txt"), "worker edit");
            Commit(scenario.Paths["default"], "seed.txt");
        }, scenario =>
        {
            if (commitInHome)
            {
                AssertCompleted(scenario.Task);
                Assert.DoesNotContain(DispatchRejectionDiagnosticMarker.Prefix, scenario.Task.LastVerification!.StandardError);
            }
            else
            {
                var diagnostic = AssertRejected(scenario.Task);
                Assert.Contains(DispatchRejectionDiagnosticMarker.Format(false, 0, "none"), diagnostic);
                Assert.Contains("commits_after_dispatch=0; changed_paths=none.", diagnostic);
            }
        });
    }

    [Fact]
    public void Rejection_InternalCommits_ReportsHomeAndEveryDeclaredRepository()
    {
        RunScenario(declared: true, scenario =>
        {
            foreach (var path in scenario.Paths.Values)
            {
                File.WriteAllText(Path.Combine(path, ".orchestrator-handoff.md"), "Internal dispatch artifact");
                Commit(path, ".orchestrator-handoff.md");
            }
        }, scenario =>
        {
            var diagnostic = AssertRejected(scenario.Task);
            Assert.True(DispatchRejectionDiagnosticMarker.TryParse(diagnostic, out var recognized,
                out var reason, out var commits, out var paths), diagnostic);
            const string expectedPaths = ".orchestrator-handoff.md | second:.orchestrator-handoff.md | third:.orchestrator-handoff.md";
            Assert.False(recognized);
            Assert.Equal(DispatchRejectionDiagnosticMarker.VerificationPatternUnmatched, reason);
            Assert.Equal(3, commits);
            Assert.Equal(expectedPaths, paths);
            Assert.Contains($"commits_after_dispatch=3; changed_paths={expectedPaths}.", diagnostic);
        });
    }

    [Fact]
    public void Collect_UndeclaredGoal_ReturnsEmptyWithoutGit()
    {
        var task = new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer);
        var goal = new AgentOrchestratorKernel().CreateGoal("Undeclared", [task]);
        var committer = new DispatchWorktreeCommitter((_, _) =>
            throw new InvalidOperationException("Undeclared goals must not inspect git."));

        var evidence = GoalRepositoryChangeEvidence.Collect(goal, task,
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), committer);

        Assert.Equal(0, evidence.CommitsAfterDispatch);
        Assert.False(evidence.HasRelevantCommitAfterDispatch);
        Assert.Equal("none", evidence.ChangedPathsSummary);
        var home = GoalWorktreeDispatchEvidence.Unknown with
        {
            CommitsAfterDispatch = 2,
            ChangedPaths = Enumerable.Range(0, 10).Select(index => $"file{index}.cs").ToArray()
        };
        var combined = evidence.CombineWithHome("unused-home", home);
        Assert.Equal(home.CommitsAfterDispatch, combined.CommitsAfterDispatch);
        Assert.Equal(home.HasRelevantCommitAfterDispatch, combined.HasRelevantCommitAfterDispatch);
        Assert.Equal(home.ChangedPathsSummary, combined.ChangedPathsSummary);
    }

    [Fact]
    public void CombineWithHome_SameResolvedWorktree_CountsOnceWithRepositoryKey()
    {
        RunScenario(declared: true, scenario =>
        {
            var path = scenario.Paths["second"];
            File.AppendAllText(Path.Combine(path, "seed.txt"), "worker edit");
            Commit(path, "seed.txt");
            var committer = new DispatchWorktreeCommitter();
            var evidence = GoalRepositoryChangeEvidence.Collect(scenario.Goal, scenario.Task,
                scenario.Task.LastDispatch!.DispatchedAt, committer);
            var home = committer.InspectGoalWorktree(path, scenario.Goal.Id, scenario.Task.LastDispatch.DispatchedAt,
                expectedBranch: GoalRepositoryWorktreeLayout.BranchName(scenario.Goal.Id, "second"));
            Assert.True(home.IsAvailable);

            var combined = evidence.CombineWithHome(path + Path.DirectorySeparatorChar, home.Evidence);

            Assert.Equal(1, combined.CommitsAfterDispatch);
            Assert.True(combined.HasRelevantCommitAfterDispatch);
            Assert.Equal("second:seed.txt", combined.ChangedPathsSummary);
        }, scenario => AssertCompleted(scenario.Task));
    }

    [Fact]
    public void Collect_ManyPaths_PreservesAllPathsAndLaterRepository()
    {
        RunScenario(declared: true, scenario =>
        {
            var files = Enumerable.Range(0, 10).Select(index => $"file{index}.txt").ToArray();
            foreach (var file in files) File.WriteAllText(Path.Combine(scenario.Paths["second"], file), "change");
            RunGit(scenario.Paths["second"], "add", "--all");
            RunGit(scenario.Paths["second"], "commit", "-m", "Many changed paths");
            File.AppendAllText(Path.Combine(scenario.Paths["third"], "seed.txt"), "worker edit");
            Commit(scenario.Paths["third"], "seed.txt");

            var evidence = GoalRepositoryChangeEvidence.Collect(scenario.Goal, scenario.Task,
                scenario.Task.LastDispatch!.DispatchedAt, new DispatchWorktreeCommitter());

            Assert.Equal(2, evidence.CommitsAfterDispatch);
            Assert.True(evidence.HasRelevantCommitAfterDispatch);
            Assert.Equal(string.Join(" | ", files.Select(file => $"second:{file}").Append("third:seed.txt")),
                evidence.ChangedPathsSummary);
        }, scenario => AssertCompleted(scenario.Task));
    }

    [Fact]
    public void Collect_WrongBranch_ReportsUnavailableAndDispatchStillFails()
    {
        RunScenario(declared: true, scenario =>
        {
            RunGit(scenario.Paths["second"], "switch", "-c", "wrong-branch");
            var evidence = GoalRepositoryChangeEvidence.Collect(scenario.Goal, scenario.Task,
                scenario.Task.LastDispatch!.DispatchedAt, new DispatchWorktreeCommitter());

            Assert.Equal(0, evidence.CommitsAfterDispatch);
            Assert.False(evidence.HasRelevantCommitAfterDispatch);
            Assert.Contains("second:unavailable (branch-mismatch;", evidence.ChangedPathsSummary);
        }, scenario =>
        {
            Assert.Equal(WorkTaskStatus.Failed, scenario.Task.Status);
            Assert.Contains("repository=second; worktree inspection refused; reason=branch-mismatch",
                scenario.Task.LastVerification!.StandardError);
        });
    }

    private static void Commit(string path, string file)
    {
        RunGit(path, "add", "--force", "--", file);
        RunGit(path, "commit", "-m", "Post-dispatch fixture change");
    }

    private static void AssertCompleted(TaskSpec task)
    {
        Assert.True(task.Status == WorkTaskStatus.Completed, task.LastVerification?.StandardError);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.Equal("succeeded-dispatch-completion-evidence", task.LastVerification.CompletionVerdictRule);
    }

    private static string AssertRejected(TaskSpec task)
    {
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing),
            task.LastVerification.StandardError);
        return task.LastVerification.StandardError;
    }

    private static void RunScenario(bool declared, Action<Scenario> prepare, Action<Scenario> assertOutcome)
    {
        var roots = new Dictionary<string, string>();
        try
        {
            foreach (var key in declared ? new[] { "default", "second", "third" } : ["default"])
                roots[key] = PastDatedSeedRepository.Create();
            var home = roots["default"];
            _ = CreateMigratedStateRepository(OrchestratorWorkspace.ForDirectory(home).SqliteStatePath);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Repository change evidence", [new TaskSpec(TaskId.New(), "Implement change", AgentRole.Developer)]);
            var paths = new Dictionary<string, string>();
            if (declared)
            {
                kernel.DeclareGoalRepositories(goal.Id, new GoalRepositoryDeclaration(roots.Keys.ToArray(), roots));
                var secondaryRoots = roots.Where(entry => entry.Key != "default").ToDictionary(entry => entry.Key, entry => entry.Value);
                Assert.True(GoalRepositoryWorktreeProvisioner.TryProvision(new GoalRepositorySet(secondaryRoots.Keys),
                    goal.Id, secondaryRoots, out var bindings, out var failure), failure);
                foreach (var binding in bindings!) paths[binding.Key] = binding.Value.WorktreePath;
            }
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            paths["default"] = GoalWorktrees.Ensure(home, goal.Id);
            var seeds = paths.ToDictionary(entry => entry.Key, entry => RunGitOutput(entry.Value, "rev-parse", "HEAD"));
            var dispatchedAt = clock.UtcNow.AddMinutes(-1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", paths["default"], dispatchedAt));
            var logs = Directory.CreateDirectory(Path.Combine(home, "logs")).FullName;
            var stdout = Path.Combine(logs, "worker.out.log");
            var stderr = Path.Combine(logs, "worker.err.log");
            var exit = Path.Combine(logs, "worker.exit.txt");
            File.WriteAllText(stdout, WorkerOutput);
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt",
                paths["default"], stdout, stderr, exit, dispatchedAt, null, null));
            var scenario = new Scenario(goal, task, paths, seeds);
            prepare(scenario);
            new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);
            assertOutcome(scenario);
        }
        finally
        {
            foreach (var root in roots.Values) PastDatedSeedRepository.Delete(root);
        }
    }

    private sealed record Scenario(Goal Goal, TaskSpec Task,
        IReadOnlyDictionary<string, string> Paths, IReadOnlyDictionary<string, string> Seeds);

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
