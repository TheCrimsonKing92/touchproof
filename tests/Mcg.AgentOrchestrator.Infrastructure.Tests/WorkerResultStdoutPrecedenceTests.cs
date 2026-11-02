using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Each runner fixture owns its temporary repository/logs and uses simulated process liveness.
public sealed class WorkerResultStdoutPrecedenceTests : WorkerDispatchTestSupport
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailingStdoutCannotBeOverridden(bool readOnly, bool hasArtifact)
    {
        var artifactProbes = 0;
        var classifier = CreateClassifier(path =>
        {
            artifactProbes++;
            return hasArtifact && Path.GetFileName(path) == "WORKER_RESULT.md";
        }, _ => Result("pass - 4/4 passed"));

        Assert.False(classifier.HasSuccessfulWorkerResult(
            "memory-worktree", Result("fail - 3/4 passed"), Result("pass - 4/4 passed"),
            allowNoChangedFiles: readOnly, requireNoBlockers: readOnly,
            roleCapability: readOnly ? DispatchRoleOutputCapability.ReadOnly : null));
        Assert.Equal(0, artifactProbes);
    }

    [Fact]
    public void PassingStdoutWinsOverFailingStderr()
    {
        Assert.True(CreateClassifier().HasSuccessfulWorkerResult(
            "memory-worktree", Result("pass - 4/4 passed"), Result("fail - 3/4 passed")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Worker is preparing its response.")]
    [InlineData("WORKER_RESULT:\nblockers: none\nEND_WORKER_RESULT")]
    public void IncompleteStdoutFallsBackToStderr(string stdout)
    {
        Assert.True(CreateClassifier().HasSuccessfulWorkerResult(
            "memory-worktree", stdout, Result("pass - 4/4 passed")));
    }

    [Fact]
    public void EmptyStreamsFallBackToWorktreeMarkdown()
    {
        var reads = new List<string>();
        var path = Path.Combine("memory-worktree", "WORKER_RESULT.md");
        var classifier = CreateClassifier(candidate => candidate == path, candidate =>
        {
            reads.Add(candidate);
            return Result("pass - 4/4 passed");
        });

        Assert.True(classifier.HasSuccessfulWorkerResult("memory-worktree", "", ""));
        Assert.Equal(new[] { path }, reads);
    }

    [Fact]
    public void FailingStderrCannotFallThroughToPassingWorktreeResult()
    {
        var probes = 0;
        var classifier = CreateClassifier(_ => { probes++; return true; },
            _ => Result("pass - 4/4 passed"));

        Assert.False(classifier.HasSuccessfulWorkerResult("memory-worktree", "", Result("fail - 3/4 passed")));
        Assert.Equal(0, probes);
    }

    [Fact]
    public void WrapperFailureIsNotReconciledFromPassingStderr()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Planner, Result("fail - 3/4 passed", "none"),
            Result("pass - 4/4 passed", "none"), clock, childExitCode: 0);
        File.WriteAllText(process.ExitCodePath, "1");

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(1, verification.ObservedRootExitCode);
        Assert.Equal(0, verification.ChildExitCode);
        Assert.False(verification.ReconciledToSuccess);
        Assert.Equal(1, verification.ExitCode);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.DoesNotContain("logical_exit_code=0", verification.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void DirtyWorktreeIsNotCommittedFromPassingStderr()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        // Developer avoids the Tester terminal contract masking the commit decision.
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root, AgentRole.Developer, Result("fail - 3/4 passed", "dirty.txt"),
            Result("pass - 4/4 passed", "dirty.txt"), clock,
            worktree => File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted"));

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, verification.ExitCode);
        Assert.Contains("left the worktree dirty", verification.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Orchestrator committed the worker's verified worktree edits",
            verification.StandardError, StringComparison.Ordinal);
        Assert.Contains("?? dirty.txt", ReadGit(process.WorkingDirectory, ["status", "--short"]),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HungWrapperIsNotRescuedFromPassingStderr()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var stdout = Path.Combine(logs, "worker.out.log");
        var stderr = Path.Combine(logs, "worker.err.log");
        var exit = Path.Combine(logs, "worker.exit.txt");
        var childExit = Path.Combine(logs, "worker.child-exit.json");
        var now = DateTimeOffset.Parse("2026-08-08T06:00:00Z");
        var startedAt = now.AddMinutes(-40);
        const string command = "claude prompt";
        File.WriteAllText(stdout, ResearcherContractFixture() + Environment.NewLine +
            PlannerContractPlanFixture() + Environment.NewLine + Result("fail - 3/4 passed", "none"));
        File.WriteAllText(stderr, Result("pass - 4/4 passed", "none"));
        File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
        File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
        File.WriteAllText(childExit, JsonSerializer.Serialize(
            new DispatchProcessHost.DispatchChildExitRecord(888888, 0, now.AddMinutes(-1)),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Hung read-only wrapper",
            [new TaskSpec(TaskId.New(), "Plan the work.", AgentRole.Planner)]);
        var agent = new AgentDefinition(new AgentId("test-planner"), "Test Planner", AgentRole.Planner,
            new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", command, root, startedAt));
        var process = new TaskProcessRecord(999999, command, root, stdout, stderr, exit, startedAt, null, null,
            ChildExitRecordPath: childExit);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running",
            new FileInfo(stdout).Length, new FileInfo(stderr).Length, childPid: null);

        var lifecycle = new SignalBackedProcessLifecycle();
        var runner = new BackgroundDispatchRunner(new TestClock(now), TimeSpan.FromMinutes(2),
            isStillRunning: lifecycle.IsStillRunning, tryKillOwnedProcess: lifecycle.TryKill);
        var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        runner.ApplyRefreshOutcomeAndWriteDiagnostics(kernel, goal.Id, task.Id, outcome);

        Assert.True(lifecycle.WasKillRequested(process.ProcessId));
        Assert.Equal(1, outcome.ProcessRecord.ExitCode);
        Assert.Equal(0, outcome.ProcessRecord.ChildExitCode);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        var verification = Assert.IsType<TaskVerificationRecord>(task.LastVerification);
        Assert.Equal(1, verification.ExitCode);
        Assert.True(DispatchExitArtifacts.TryRead(exit, out var exitArtifact));
        Assert.Equal(1, exitArtifact.ExitCode);
        Assert.Contains("wrapper appears hung", verification.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rescue denied", verification.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wrapper process reaped; task completed", verification.StandardError,
            StringComparison.Ordinal);
    }

    private static string Result(string tests, string files = "src/Feature.cs") =>
        WorkerResultBlock(files, "source inspection", tests, commit: "none");

    private static WorkerDispatchCompletionClassifier CreateClassifier(
        Func<string, bool>? fileExists = null, Func<string, string>? readArtifactText = null) =>
        new(dispatch => WorkerProviderCatalog.Default().ResolveProfile(dispatch.WorkerName),
            new TestClock(DateTimeOffset.Parse("2026-08-22T12:00:00Z")),
            fileExists ?? (_ => throw new InvalidOperationException("Streams must prevent artifact fallback.")),
            readArtifactText ?? (_ => throw new InvalidOperationException("Streams must prevent artifact reads.")));

    private sealed class SignalBackedProcessLifecycle
    {
        private readonly HashSet<int> _killRequests = [];
        public bool IsStillRunning(int processId) => !_killRequests.Contains(processId);
        public bool TryKill(int processId) => _killRequests.Add(processId);
        public bool WasKillRequested(int processId) => _killRequests.Contains(processId);
    }
}
