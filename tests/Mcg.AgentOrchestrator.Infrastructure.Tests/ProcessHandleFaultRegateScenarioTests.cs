using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProcessHandleFaultRegateScenarioTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    private const string Scope = "src/ProcessHandle.cs";
    private const string ProcessFault = "Process was not started by this object, so requested information cannot be determined.";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void WrappedFaultReturnsToVerifiedUntilTheSharedRetryBudgetIsSpent()
    {
        var root = CreateTempDirectory("mcg-process-handle-regate");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
            var task = goal.Tasks.Single();
            var now = DateTimeOffset.UnixEpoch;
            var gateCalls = 0;
            var workerRetries = 0;
            var escalations = new List<string>();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                root, TrunkBranchName.Default, runInline: true, utcNow: () => now,
                buildStorageRoot: new DotnetBuildStorageRoot(Path.Combine(root, "test-dotnet")));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) =>
                {
                    gateCalls++;
                    throw new Exception("gate wrapper", new InvalidOperationException(ProcessFault));
                },
                getLandingFileScopes: _ => [Scope],
                parallelAcceptanceAttemptCoordinator: coordinator,
                utcNow: () => now,
                retryTask: (goalId, taskId, reason) =>
                {
                    workerRetries++;
                    return kernel.RetryTask(goalId, taskId, reason);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                writeEscalation: (_, _, reason) => escalations.Add(reason));

            BatchLoopSummary Tick()
            {
                now = now.AddSeconds(1);
                return new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1);
            }

            const int cap = ConductorBatchLoop.ParallelAcceptanceTransientFailureCap;
            for (var count = 1; count <= cap; count++)
            {
                var held = Tick();
                Assert.Equal(1, held.Held);
                Assert.Equal(0, held.Escalated);
                Assert.Equal(count, gateCalls);
                var attempt = LatestAttempt(root, goal);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, attempt.Outcome);
                Assert.Equal(count, attempt.TransientFailureCount);
                Assert.NotNull(attempt.ReconciledAt);
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.Null(goal.LatestAcceptanceFailure);
                Assert.Equal(0, task.CriterionRetryCount);
                Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            }

            var atCap = Tick();
            Assert.Equal(0, atCap.Held);
            Assert.Equal(1, atCap.Escalated);
            Assert.Equal(cap + 1, gateCalls);
            Assert.Equal(cap, LatestAttempt(root, goal).TransientFailureCount);
            Assert.Contains(goal.Timeline, evt =>
                evt.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
            Assert.StartsWith("background acceptance process-handle fault: ", Assert.Single(escalations),
                StringComparison.Ordinal);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(0, workerRetries);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameMessageOnAnotherTypeOrAnUnrelatedFaultStillEscalates(bool useProcessMessage)
    {
        var root = CreateTempDirectory("mcg-process-handle-unclassified");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
            var escalations = new List<string>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw (useProcessMessage
                    ? new ArgumentException(ProcessFault)
                    : new InvalidOperationException("unrelated gate fault")),
                getLandingFileScopes: _ => [Scope],
                parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                    root, TrunkBranchName.Default, runInline: true,
                    buildStorageRoot: new DotnetBuildStorageRoot(Path.Combine(root, "test-dotnet"))),
                writeEscalation: (_, _, reason) => escalations.Add(reason));

            var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1);

            Assert.Equal(0, summary.Held);
            Assert.Equal(1, summary.Escalated);
            Assert.StartsWith("parallel acceptance fault: ", Assert.Single(escalations), StringComparison.Ordinal);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(0, LatestAttempt(root, goal).TransientFailureCount);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void FailingTestVerdictWinsOverProcessHandleFault()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], "branch", "main");
        var acceptance = new AcceptanceVerificationSummary(false, [], FailedChecks: ["tests"]);
        var run = ConductorParallelAcceptanceRunResult.Accepted(candidate, acceptance) with
        {
            Exception = new InvalidOperationException(ProcessFault)
        };
        var attempt = CreateAttempt(goal, transientCount: 0);

        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);

        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        Assert.NotNull(goal.LatestAcceptanceFailure);
    }

    [Theory]
    [InlineData(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap - 1, true)]
    [InlineData(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, false)]
    public void CompletionUsesTheAvailableRetryBudget(int transientCount, bool canRegate)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope], "branch", "main");
        var run = ConductorParallelAcceptanceRunResult.Fault(candidate, new InvalidOperationException(ProcessFault));
        var attempt = CreateAttempt(goal, transientCount);

        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
        var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
            MakeDriver(), ConductorAutonomyPolicy.Conservative, run, attempt, out _);

        if (canRegate)
        {
            Assert.Equal(GoalLifecycleState.Verified, Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome).State);
            Assert.Equal(GoalStatus.Verified, goal.Status);
        }
        else
        {
            Assert.Equal(ConductorEscalationKind.BackgroundAcceptanceFailed,
                Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Kind);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
        }
        Assert.Null(goal.LatestAcceptanceFailure);
    }

    private static ConductorParallelAcceptanceAttempt CreateAttempt(Goal goal, int transientCount) =>
        new("attempt", goal.Id.Value, goal.Id.Value[..8], 0, "branch", "main",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1,
            ConductorParallelAcceptanceAttemptOutcome.Failed, "stdout", "stderr", "exit", "heartbeat",
            "result", "metadata", TransientFailureCount: transientCount);

    private static ConductorParallelAcceptanceAttempt LatestAttempt(string root, Goal goal) =>
        Directory.EnumerateFiles(Path.Combine(root, goal.Id.Value), "*.attempt.json")
            .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .OrderByDescending(attempt => attempt.StartedAt).First();
}
