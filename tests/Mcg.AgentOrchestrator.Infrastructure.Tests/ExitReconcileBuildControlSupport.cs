using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class ExitReconcileBuildControlSupport
{
    internal static async Task TerminalSweep_InapplicableBuild_RejectsWithoutConsumption(
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

    internal static void AssertRejected(ExitReconcileBuildFixture fixture, string diagnostic)
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
