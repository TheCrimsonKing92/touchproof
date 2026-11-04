using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all negative stimuli affect only fixture-owned files and canonical state.
public sealed class ExitReconcileBuildControlTests
{
    [Theory]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("preflight", false)]
    [InlineData("preflight", true)]
    public async Task OrderedReconcilers_SettledSuccess_IsConsumedAndCompletedOnce(
        string other, bool terminalFirst)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        int Other() => other == "batch" ? fixture.BatchRecovery() : fixture.DispatchPreflight();

        Assert.Equal(1, terminalFirst ? fixture.TerminalSweep() : Other());
        fixture.AssertSuccess(invocation);
        var completion = Assert.Single(fixture.Task.VerificationHistory);
        var head = fixture.Build.Git("rev-parse", "HEAD");
        fixture.Save();
        fixture.Reload();

        Assert.Equal(0, terminalFirst ? Other() : fixture.TerminalSweep());
        // Repeating the same entry point is also inert after reloading its settled state.
        Assert.Equal(0, fixture.TerminalSweep());

        fixture.AssertSuccess(invocation);
        Assert.Equal(completion.CompletedAt, Assert.Single(fixture.Task.VerificationHistory).CompletedAt);
        Assert.Equal(head, fixture.Build.Git("rev-parse", "HEAD"));
        Assert.Single(fixture.Goal.Timeline.Where(evt => evt.TaskId == fixture.Build.TaskId &&
            evt.Kind == ProgressKind.TaskCompleted));
    }

    [Theory]
    [InlineData("missing", "missing-current-candidate-outcome")]
    [InlineData("prior-dispatch", "history-invalid")]
    [InlineData("other-dispatch", "history-invalid")]
    [InlineData("tree", "missing-current-candidate-outcome")]
    [InlineData("scope", "scope-invalid")]
    [InlineData("coverage", "history-invalid")]
    [InlineData("worktree", "history-invalid")]
    [InlineData("invalid-apparatus", "history-invalid")]
    public async Task TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(
        string control, string diagnostic)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        if (control == "tree")
            File.AppendAllText(Path.Combine(fixture.Build.RepositoryRoot, WorkerBuildRequestFixture.Source),
                "\npublic class AfterBuild { }");
        else
            fixture.RewriteTask(task => control switch
            {
                "missing" => task with { WorkerBuildInvocations = [] },
                "scope" => task with { LastDispatch = task.LastDispatch! with
                {
                    WorkerBuildScope = task.LastDispatch.WorkerBuildScope! with { RetainedMetadataBase64 = "invalid!" }
                } },
                _ => task with { WorkerBuildInvocations = [control switch
                {
                    "prior-dispatch" => invocation with
                    {
                        DispatchAt = invocation.DispatchAt.AddTicks(-1), DispatchId = "prior-dispatch"
                    },
                    "other-dispatch" => invocation with { DispatchId = "other-unconsumed-dispatch" },
                    "worktree" => invocation with { WorktreeRoot = fixture.Build.Root },
                    "invalid-apparatus" => invocation with
                    {
                        Projects = [], Children = [], Outcome = WorkerBuildOutcome.CapacityBeforeStart, CompilerExitCode = null,
                        Observations = [new(WorkerBuildPhase.Requested, invocation.DispatchAt),
                            new(WorkerBuildPhase.WaitingForBuildSlot, invocation.DispatchAt),
                            new(WorkerBuildPhase.Completed, invocation.DispatchAt)]
                    },
                    _ => invocation with { Projects = [] }
                }] }
            });

        fixture.Reload();
        switch (control)
        {
            case "missing":
                Assert.Empty(fixture.Task.WorkerBuildInvocations);
                break;
            case "scope":
                Assert.Equal("invalid!", fixture.Task.LastDispatch!.WorkerBuildScope!.RetainedMetadataBase64);
                break;
            case "tree":
                Assert.True(WorktreeTreeDigest.TryCompute(fixture.Build.RepositoryRoot, out var digest, out _));
                Assert.NotEqual(invocation.TreeDigest, digest);
                break;
            case "coverage":
            case "invalid-apparatus":
                Assert.Empty(fixture.StoredInvocation().Projects);
                break;
            case "worktree":
                Assert.NotEqual(invocation.WorktreeRoot, fixture.StoredInvocation().WorktreeRoot);
                break;
            default:
                Assert.NotEqual(invocation.DispatchId, fixture.StoredInvocation().DispatchId);
                Assert.False(fixture.StoredInvocation().Consumed);
                break;
        }

        var runner = fixture.ControlledCompletionRunner();
        Assert.Equal(1, fixture.TerminalSweep(runner: runner));

        if (control is "missing" or "tree")
        {
            var operation = Assert.IsType<CompletionBuildEvidenceOperation>(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(CompletionBuildEvidencePhase.Pending, operation.Phase);
            Assert.Equal(new[] { WorkerBuildRequestFixture.Project }, operation.Projects);
            Assert.True(WorktreeTreeDigest.TryCompute(fixture.Build.RepositoryRoot, out var currentDigest, out _));
            Assert.Equal(currentDigest, operation.TreeDigest);
            Assert.Equal(fixture.Task.LastDispatch!.DispatchedAt, operation.DispatchAt);
            Assert.Null(fixture.Task.LastVerification);
            Assert.Null(fixture.Task.LastDispatch.ResultCommit);
            Assert.Equal(1, fixture.CompletionLaunches);
            fixture.Save();
            fixture.Reload();
            runner = fixture.ControlledCompletionRunner();
            runner.RefreshLatestProcess(fixture.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
            Assert.Equal(operation.Id, fixture.Task.CompletionBuildEvidence!.Id);
            Assert.Equal(1, fixture.CompletionLaunches);
            Assert.Null(fixture.Task.LastVerification);
            fixture.ReleaseCompletion();
            runner.RefreshLatestProcess(fixture.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
            Assert.Equal(WorkTaskStatus.Completed, fixture.Task.Status);
            Assert.True(fixture.Task.LastVerification!.CompletionVerdictVerifiedSuccess, fixture.Describe());
            Assert.True(fixture.Task.CompletionBuildEvidence.Applied);
            Assert.NotNull(fixture.Task.LastDispatch.ResultCommit);
            Assert.Single(fixture.Task.VerificationHistory);
            Assert.Equal(1, fixture.CompletionLaunches);
        }
        else
        {
            AssertRejected(fixture, "worker_build_host=" + diagnostic);
            Assert.Null(fixture.Task.CompletionBuildEvidence);
            Assert.Equal(0, fixture.CompletionLaunches);
        }
        var stored = fixture.Build.Operations.Load(fixture.Build.GoalId.Value)
            .GetTask(fixture.Build.GoalId, fixture.Build.TaskId).WorkerBuildInvocations;
        if (control == "missing") Assert.Empty(stored);
        else
        {
            var after = Assert.Single(stored);
            Assert.Equal(invocation.Id, after.Id);
            Assert.False(after.Consumed);
            Assert.Equal(invocation.Version, after.Version);
        }
    }

    [Fact]
    public async Task TerminalSweep_CompilerFailure_RemainsCandidateFailure()
    {
        using var fixture = new ExitReconcileBuildFixture(compilerFailure: true);
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.CompilerFailure, invocation.Outcome);

        Assert.Equal(1, fixture.TerminalSweep());

        AssertRejected(fixture, "outcome=CompilerFailure");
        Assert.Null(fixture.Task.LastVerification!.OrchestratorFailureReason);
        Assert.True(fixture.StoredInvocation().Consumed);
        Assert.Equal(invocation.Version + 1, fixture.StoredInvocation().Version);
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("batch")]
    [InlineData("preflight")]
    public async Task ProductionComposition_SeparateStore_CannotBorrowNativeSuccess(string path)
    {
        using var fixture = new ExitReconcileBuildFixture();
        var invocation = await fixture.CompleteNativeDispatch();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var foreignRoot = Path.Combine(fixture.Build.Root, "foreign-authority");
        Directory.CreateDirectory(foreignRoot);
        var foreign = fixture.Workspace with
        {
            OrchestratorDirectory = foreignRoot,
            SqliteStatePath = Path.Combine(foreignRoot, "state.db"),
            LogDirectory = Path.Combine(foreignRoot, "logs")
        };
        var snapshot = fixture.Kernel.ExportGoalSnapshot(fixture.Build.GoalId);
        var foreignKernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([
            snapshot with { Tasks = snapshot.Tasks.Select(task => task with { WorkerBuildInvocations = [] }).ToArray() }
        ], []));
        _ = StateDbMigrations.EnsureUpToDate(foreign.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(foreign.SqliteStatePath).SaveAsync(foreignKernel);

        fixture.Kernel = foreignKernel;
        int Reconcile() => path switch
        {
            "terminal" => fixture.TerminalSweep(foreign),
            "batch" => fixture.BatchRecovery(foreign),
            _ => fixture.DispatchPreflight(foreign)
        };

        Assert.Equal(1, Reconcile());
        AssertRejected(fixture, "worker_build_host=enrollment-invalid");
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        var repository = new SqliteOrchestratorStateRepository(foreign.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        fixture.Kernel = await repository.LoadGoalsAsync([fixture.Build.GoalId]);
        Assert.Equal(0, Reconcile());
        AssertRejected(fixture, "worker_build_host=enrollment-invalid");
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.False(fixture.StoredInvocation().Consumed);
        Assert.Equal(invocation.Version, fixture.StoredInvocation().Version);
        Assert.Empty((await new SqliteOrchestratorStateRepository(foreign.SqliteStatePath)
            .LoadGoalsAsync([fixture.Build.GoalId])).GetTask(fixture.Build.GoalId, fixture.Build.TaskId).WorkerBuildInvocations);
    }

    private static void AssertRejected(ExitReconcileBuildFixture fixture, string diagnostic)
    {
        Assert.NotNull(fixture.Task.LastVerification);
        Assert.False(fixture.Task.LastVerification.Succeeded, fixture.Describe());
        Assert.False(fixture.Task.LastVerification.CompletionVerdictVerifiedSuccess, fixture.Describe());
        Assert.Contains(diagnostic, fixture.Task.LastVerification.StandardError);
        Assert.NotEqual(WorkTaskStatus.Completed, fixture.Task.Status);
        Assert.Null(fixture.Task.LastDispatch!.ResultCommit);
        Assert.DoesNotContain(fixture.Task.VerificationHistory, verification => verification.Succeeded);
        Assert.Contains(" M " + WorkerBuildRequestFixture.Source,
            fixture.Build.Git("status", "--short").Replace('\r', ' '));
    }
}
