using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repositories/SQLite, production consumer, injected child lifecycle.
public sealed class WorkerBuildApplicabilityCompletionTests
{
    [Theory]
    [InlineData("missing", "no-invocation-recorded")]
    [InlineData("tree", "tree-mismatch")]
    [InlineData("corrupt-digest", "unresolved-candidate")]
    [InlineData("scope", "unresolved-scope")]
    [InlineData("candidate", "unresolved-candidate")]
    public void Refresh_InapplicableEvidence_PersistsExplanationAndFailureDetail(
        string control, string expectedReason)
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var originalProcess = fixture.Task.LastProcess!;
        if (control is "tree" or "corrupt-digest")
            fixture.SetInvocations(fixture.Invocation("00000001", digest: control == "tree"
                ? WorktreeTreeDigest.Algorithm + ":" + new string('1', 40) : "corrupt-recorded-digest"));
        if (control == "scope")
        {
            fixture.Mutate(task => task with { LastDispatch = task.LastDispatch! with
                { WorkerBuildScope = task.LastDispatch.WorkerBuildScope! with { RetainedMetadataBase64 = "invalid!" } } });
            fixture.Save();
        }
        if (control == "candidate")
            fixture.DigestGitRunner = (_, _, _) => new(1, "", "controlled missing candidate evidence", false);
        var receipt = fixture.Consume();
        Assert.True(receipt.MissingEvidence);
        Assert.Contains(expectedReason, WorkerBuildApplicabilityFormatter.Format(receipt.Applicability!));
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        var payload = Assert.IsType<DispatchDiagnosticPayload>(outcome.DiagnosticPayload);
        var verification = Assert.IsType<TaskVerificationRecord>(fixture.Task.LastVerification);
        Assert.Contains("worker_build_applicability=" + expectedReason + "; next=", payload.StandardError);
        Assert.Contains("worker_build_applicability=" + expectedReason + "; next=", verification.StandardError);
        Assert.Equal(1, verification.ExitCode);
        Assert.Equal(0, verification.ObservedRootExitCode);
        Assert.Equal(originalProcess.ChildExitCode, verification.ChildExitCode);
        Assert.False(verification.ReconciledToSuccess);
        Assert.False(verification.Succeeded);
        Assert.Equal(0, fixture.Build.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.All(fixture.Task.WorkerBuildInvocations, i => Assert.False(i.Consumed));
        var failure = DispatchFailureClassifier.Classify(fixture.Task, verification);
        Assert.Equal("worker-build-evidence-missing", TaskOutcomeClassifier.TryExtractRule(failure.ClassifierReceipt));
        Assert.Contains("worker_build_applicability=" + expectedReason + "; next=", failure.EvidenceSummary);
        fixture.Save();
        fixture.Build.Reload();
        Assert.Equal(verification.StandardError, fixture.Task.LastVerification!.StandardError);
        Assert.Equal(failure.EvidenceSummary,
            DispatchFailureClassifier.Classify(fixture.Task, fixture.Task.LastVerification).EvidenceSummary);
        Assert.Contains(fixture.Build.Goal.Timeline, entry => entry.TaskId == fixture.Build.TaskId &&
            entry.Kind == ProgressKind.TaskFailed && entry.Message.Contains("worker_build_applicability=" + expectedReason + "; next=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.Success)]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    public void Refresh_RecoveryReplacement_RetainsReasonAndSingleAttempt(CompletionBuildEvidencePhase phase)
    {
        var fixture = new WorkerBuildApplicabilityFixture(scopeComplete: true);
        fixture.Runner().RefreshLatestProcess(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        var operation = Assert.IsType<CompletionBuildEvidenceOperation>(fixture.Task.CompletionBuildEvidence);
        Assert.True(operation.IsPending);
        Assert.Null(fixture.Task.LastVerification);
        Assert.Equal(1, fixture.Build.Launches);
        Assert.Equal(1, fixture.Build.Authorizations);
        fixture.Build.Reload();
        fixture.Runner().RefreshLatestProcess(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        Assert.Equal(operation.Id, fixture.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(1, fixture.Build.Launches);
        fixture.Build.Release(phase);
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        Assert.Equal(phase == CompletionBuildEvidencePhase.Success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(phase == CompletionBuildEvidencePhase.Success, fixture.Task.LastVerification!.Succeeded);
        Assert.Contains("worker_build_applicability=no-invocation-recorded; next=", outcome.DiagnosticPayload!.StandardError);
        Assert.Contains("worker_build_applicability=no-invocation-recorded; next=", fixture.Task.LastVerification.StandardError);
        if (phase == CompletionBuildEvidencePhase.Success) Assert.True(fixture.Task.CompletionBuildEvidence.Applied);
        Assert.Equal(1, fixture.Build.Launches);
        Assert.Empty(fixture.Task.WorkerBuildInvocations);
        Assert.Equal(0, fixture.Task.WorkerBuildCheckRecoveryCount);
        Assert.Single(fixture.Task.DispatchHistory);
        fixture.Save();
        fixture.Build.Reload();
        Assert.Contains("worker_build_applicability=no-invocation-recorded; next=", fixture.Task.LastVerification!.StandardError);
    }

    [Theory]
    [InlineData(WorkerBuildOutcome.Success)]
    [InlineData(WorkerBuildOutcome.CompilerFailure)]
    public void Refresh_ApplicableInvocation_PreservesConsumptionAndExitClassification(WorkerBuildOutcome result)
    {
        var fixture = new WorkerBuildApplicabilityFixture(scopeComplete: true);
        fixture.SetInvocations(fixture.Invocation("00000001", result));
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        var verification = fixture.Task.LastVerification!;
        Assert.Equal(result == WorkerBuildOutcome.Success ? 0 : 1, verification.ExitCode);
        Assert.Equal(0, verification.ObservedRootExitCode);
        Assert.Equal(result == WorkerBuildOutcome.Success, verification.Succeeded);
        Assert.True(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
        Assert.Equal(0, fixture.Build.Launches);
        Assert.Null(fixture.Task.CompletionBuildEvidence);
        Assert.Contains("applicability_invocation: id=00000001", outcome.DiagnosticPayload!.StandardError);
        if (result == WorkerBuildOutcome.CompilerFailure)
        {
            var failure = DispatchFailureClassifier.Classify(fixture.Task, verification);
            Assert.Equal("worker-build-check-failed", TaskOutcomeClassifier.TryExtractRule(failure.ClassifierReceipt));
            Assert.Contains("terminal-non-success; next=inspect", failure.EvidenceSummary);
        }
        fixture.Save();
        fixture.Build.Reload();
        Assert.True(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
    }

    [Fact]
    public void Refresh_LargeHistory_BoundsDiagnosticAndExcludesUnrelatedTasks()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        var seed = fixture.Invocation("00000000", digest: WorktreeTreeDigest.Algorithm + ":" + new string('1', 40));
        fixture.SetInvocations(Enumerable.Range(0, 30).Select(i => seed with
            { Id = i.ToString("D8"), RequestKey = i.ToString("D8") }).ToArray());
        var snapshot = fixture.Build.Kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        var unrelated = Assert.Single(goal.Tasks) with { Id = "unrelated-task",
            WorkerBuildInvocations = [seed with { Id = "UNRELATED_TASK_SENTINEL", TaskId = "unrelated-task" }] };
        fixture.Build.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with
            { Tasks = goal.Tasks.Append(unrelated).ToArray() }, goal with { Id = "unrelated-goal", Tasks = [unrelated with
            { WorkerBuildInvocations = [seed with { Id = "UNRELATED_GOAL_SENTINEL", GoalId = "unrelated-goal", TaskId = "unrelated-task" }] }] }] }, fixture.Build.Clock);
        fixture.Save();
        var receipt = fixture.Consume();
        var formatted = WorkerBuildApplicabilityFormatter.Format(receipt.Applicability!);
        Assert.True(formatted.Length <= WorkerBuildApplicabilityFormatter.RenderLimit);
        Assert.Contains("applicability_omitted_invocations=22", formatted);
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        var diagnostic = outcome.DiagnosticPayload!.StandardError;
        Assert.Contains(formatted, diagnostic);
        Assert.DoesNotContain("UNRELATED_TASK_SENTINEL", diagnostic);
        Assert.DoesNotContain("UNRELATED_GOAL_SENTINEL", diagnostic);
        Assert.DoesNotContain("unrelated-goal", diagnostic);
        Assert.DoesNotContain("00000029", diagnostic);
        Assert.Contains("changed_paths=unknown", diagnostic);
        fixture.Save();
        fixture.Build.Reload();
        Assert.Contains(formatted, fixture.Task.LastVerification!.StandardError);
        Assert.Equal(0, fixture.Build.Launches);
    }

    [Fact]
    public void Refresh_ComparableImmutableTrees_PersistsRelativePathEvidence()
    {
        var fixture = new WorkerBuildApplicabilityFixture();
        string Tree()
        {
            Assert.True(GitCli.RunForRepository(fixture.Root, "add", "-A").Succeeded);
            var result = GitCli.RunForRepository(fixture.Root, "write-tree");
            Assert.True(result.Succeeded, result.Error);
            return WorktreeTreeDigest.Algorithm + ":" + result.Output.Trim();
        }
        var recorded = Tree();
        File.AppendAllText(Path.Combine(fixture.Root, WorkerBuildApplicabilityFixture.Source), "\nclass Different { }");
        var current = Tree();
        fixture.SetInvocations(fixture.Invocation("00000001", digest: recorded));
        var receipt = fixture.Consume();
        Assert.Equal(current, receipt.Applicability!.TreeDigest);
        Assert.Equal(recorded, Assert.Single(receipt.Applicability.Invocations).TreeDigest);
        Assert.Equal(new[] { WorkerBuildApplicabilityFixture.Source }, Assert.Single(receipt.Applicability.Invocations).ChangedPaths);
        var outcome = fixture.Runner().RefreshLatestProcessWithOutcome(fixture.Build.Kernel, fixture.Build.GoalId, fixture.Build.TaskId);
        Assert.Contains("changed_paths=[src/Feature/Feature.cs]", outcome.DiagnosticPayload!.StandardError);
        fixture.Save();
        fixture.Build.Reload();
        Assert.Contains("changed_paths=[src/Feature/Feature.cs]", fixture.Task.LastVerification!.StandardError);
        Assert.Equal(0, fixture.Build.Launches);
        Assert.False(Assert.Single(fixture.Task.WorkerBuildInvocations).Consumed);
    }
}
