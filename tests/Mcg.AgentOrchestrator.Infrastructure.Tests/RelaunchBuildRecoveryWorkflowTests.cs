using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories/SQLite files, injected clocks and fake child lifecycle.
public sealed class RelaunchBuildRecoveryWorkflowTests
{
    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public async Task RelaunchRequestsOneDurableRecoveryAndRefreshAppliesSuccess(AgentRole role)
    {
        var f = FailedFixture(role);
        var original = f.Task.CompletionBuildEvidence!;
        var dispatches = f.Task.DispatchHistory.Select(d => d.DispatchedAt).ToArray();
        var baseline = f.Kernel.ExportSnapshot().Goals.Single();
        var requests = 0;
        var coordinator = f.Coordinator();
        var context = Context(f, target =>
        {
            requests++;
            return coordinator.RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId, target);
        });

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Decide(f, context));
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, held.Owner);
        Assert.DoesNotContain("automatic attempt exhausted", held.Reason);
        var recovery = f.Task.CompletionBuildEvidence!;
        Assert.NotEqual(original.Id, recovery.Id);
        Assert.Equal(original.Id, recovery.RecoversOperationId);
        Assert.Equal(original.DispatchId, recovery.DispatchId);
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, recovery.Phase);
        Assert.Null(recovery.ChildPid);
        Assert.Equal(1, requests);
        Assert.Null(Decide(f, context));
        Assert.Equal(1, requests);

        f.Reload(); // Request is durable before any child launch.
        Assert.Equal(recovery.Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(original.Id, f.Task.CompletionBuildEvidence.RecoversOperationId);
        Assert.Equal(recovery.Id, f.Coordinator().RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(original)).Id);
        Assert.False(DispatchProcessCompletionState.HasAlreadyBeenApplied(f.Task, f.Task.LastProcess!));
        var runner = f.Runner();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, f.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(1, f.Authorizations);
        Assert.Null(Decide(f, context));

        f.Release();
        f.Coordinator().ReconcileAttached(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(f.Task.CompletionBuildEvidence));
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence.Phase);
        Assert.False(f.Task.CompletionBuildEvidence.Applied);
        Assert.Null(Decide(f, context));
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
        Assert.True(f.Task.LastVerification!.Succeeded);
        Assert.True(f.Task.CompletionBuildEvidence.Applied);
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(recovery.Id, f.Task.CompletionBuildEvidence.Id);
        Assert.Equal(dispatches, f.Task.DispatchHistory.Select(d => d.DispatchedAt).ToArray());
        Assert.Equal(0, f.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, f.Task.CriterionRetryCount);
        Assert.Equal(1, requests);
        var launches = f.Launches;
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(launches, f.Launches);

        // A stale original snapshot cannot erase an independently persisted, already advanced recovery.
        var repository = new SqliteOrchestratorStateRepository(f.StatePath);
        await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, f.Kernel.ExportSnapshot().Goals.Single())]);
        f.Reload();
        Assert.Equal(recovery.Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.True(f.Task.CompletionBuildEvidence.Applied);
        Assert.Equal(WorkTaskStatus.Completed, f.Task.Status);
    }

    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void AppliedSuccess_TaskFailedForAnotherReason_DoesNotHold(AgentRole role)
    {
        var f = FailedFixture(role);
        var requests = 0;
        var context = Context(f, target =>
        {
            requests++;
            return f.Coordinator().RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId, target);
        });
        Assert.NotNull(Decide(f, context));
        var runner = f.Runner();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        f.Release();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.True(f.Task.CompletionBuildEvidence!.Applied);
        Assert.Equal(CompletionBuildEvidencePhase.Success, f.Task.CompletionBuildEvidence.Phase);

        // A passing build does not resolve an independent worker verification failure.
        var snapshot = f.Kernel.ExportSnapshot();
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Tasks = goal.Tasks.Select(task => task with
                {
                    Status = WorkTaskStatus.Failed,
                    LastVerification = new TaskVerificationSnapshot("worker verification",
                        task.LastProcess!.WorkingDirectory, 1, "", "controlled non-build failure", f.Clock.UtcNow,
                        OrchestratorFailureReason: "worker-output-invalid"),
                    VerificationHistory = []
                }).ToArray()
            }).ToArray()
        }, f.Clock);

        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        Assert.False(f.Task.LastVerification!.Succeeded);
        Assert.Null(Decide(f, context));
        Assert.Equal(1, requests);
        Assert.Equal(0, f.Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, f.Task.CriterionRetryCount);
    }

    [Theory]
    [InlineData(CompletionBuildEvidencePhase.ApparatusFailure)]
    [InlineData(CompletionBuildEvidencePhase.CompilerFailure)]
    [InlineData(CompletionBuildEvidencePhase.TimedOut)]
    [InlineData(CompletionBuildEvidencePhase.StaleCandidate)]
    [InlineData(CompletionBuildEvidencePhase.Cancelled)]
    public void AnyAppliedRecoveryFailureExhaustsAllowance(CompletionBuildEvidencePhase phase)
    {
        var f = FailedFixture();
        var original = f.Task.CompletionBuildEvidence!;
        var requests = 0;
        var context = Context(f, target =>
        {
            requests++;
            return f.Coordinator().RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId, target);
        });
        Assert.NotNull(Decide(f, context));
        var recoveryId = f.Task.CompletionBuildEvidence!.Id;
        var runner = f.Runner();
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(CompletionBuildEvidencePhase.Pending, f.Task.CompletionBuildEvidence.Phase);
        f.Release(phase, receipt: false);
        runner.RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.True(f.Task.CompletionBuildEvidence.Applied);
        Assert.Equal(phase, f.Task.CompletionBuildEvidence.Phase);
        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Decide(f, context));
        Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
        Assert.Contains($"operation={recoveryId}; outcome={phase}; automatic attempt exhausted", held.Reason);
        Assert.Equal(1, requests);
        Assert.Equal(recoveryId, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(original.Id, f.Task.CompletionBuildEvidence.RecoversOperationId);
        Assert.Throws<InvalidOperationException>(() => f.Coordinator().RequestRelaunchRecovery(f.Kernel,
            f.GoalId, f.TaskId, CompletionBuildEvidenceTarget.From(f.Task.CompletionBuildEvidence)));
        Assert.Equal(1, f.Authorizations);
    }

    [Fact]
    public async Task ConcurrentStaleKernelsAdoptOneRecoveryAndStaleMergeCannotEraseIt()
    {
        var f = FailedFixture();
        var original = f.Task.CompletionBuildEvidence!;
        var baseline = f.Kernel.ExportSnapshot().Goals.Single();
        var other = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot(), f.Clock);
        var target = CompletionBuildEvidenceTarget.From(original);
        var results = await Task.WhenAll(
            Task.Run(() => f.Coordinator().RequestRelaunchRecovery(f.Kernel, f.GoalId, f.TaskId, target)),
            Task.Run(() => f.Coordinator().RequestRelaunchRecovery(other, f.GoalId, f.TaskId, target)));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.All(results, operation => Assert.Equal(original.Id, operation.RecoversOperationId));
        Assert.Equal(0, f.Authorizations);

        var repository = new SqliteOrchestratorStateRepository(f.StatePath);
        var stale = baseline with { Tasks = baseline.Tasks.Select(task => task with { Description = task.Description + " observed" }).ToArray() };
        await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(baseline, stale)]);
        f.Reload();
        Assert.Equal(results[0].Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(CompletionBuildEvidencePhase.WaitingForLease, f.Task.CompletionBuildEvidence.Phase);
    }

    [Fact]
    public async Task SnapshotMergeAdmitsNamedRecoveryButRejectsAnUnrelatedReplacement()
    {
        var f = FailedFixture();
        var baseline = f.Kernel.ExportSnapshot().Goals.Single();
        var original = f.Task.CompletionBuildEvidence!;
        var recovery = original with { Id = Guid.NewGuid().ToString("N"), RecoversOperationId = original.Id,
            Phase = CompletionBuildEvidencePhase.WaitingForLease, Applied = false, CustodyReleased = false };
        var repository = new SqliteOrchestratorStateRepository(f.StatePath);
        // Independently advance the stored row so both requests exercise the merge, not the
        // unchanged-baseline save path. Preserve the original operation's recovery eligibility.
        f.Kernel.RecordCompletionBuildEvidence(f.GoalId, f.TaskId, original with
            { Diagnostic = original.Diagnostic + " stored checkpoint" });
        await repository.SaveAsync(f.Kernel);
        Assert.True(TaskSpec.IsRelaunchRecoveryOf(f.Task.CompletionBuildEvidence!, recovery));
        var unrelated = baseline with { Tasks = baseline.Tasks.Select(t => t with
            { CompletionBuildEvidence = recovery with { RecoversOperationId = null } }).ToArray() };
        var rejected = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, unrelated)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, rejected.Disposition);
        f.Reload();
        Assert.Equal(original.Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(original.Diagnostic + " stored checkpoint", f.Task.CompletionBuildEvidence.Diagnostic);

        var current = baseline with { Tasks = baseline.Tasks.Select(t => t with { CompletionBuildEvidence = recovery }).ToArray() };
        var admitted = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync(
            [new GoalSnapshotSaveRequest(baseline, current)]));
        Assert.Equal(GoalSnapshotSaveDisposition.Merged, admitted.Disposition);
        f.Reload();
        Assert.Equal(recovery.Id, f.Task.CompletionBuildEvidence!.Id);
        Assert.Equal(original.Id, f.Task.CompletionBuildEvidence.RecoversOperationId);
    }

    internal static CompletionBuildEvidenceFixture FailedFixture(AgentRole role = AgentRole.Developer)
    {
        var f = new CompletionBuildEvidenceFixture(role: role) { FailLaunch = true };
        f.Runner().RefreshLatestProcess(f.Kernel, f.GoalId, f.TaskId);
        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        Assert.True(f.Task.CompletionBuildEvidence!.Applied);
        Assert.Equal(CompletionBuildEvidencePhase.ApparatusFailure, f.Task.CompletionBuildEvidence.Phase);
        Assert.Null(f.Task.CompletionBuildEvidence.ChildPid);
        Assert.Equal(0, f.Authorizations);
        // Refresh applies the failure in memory; persist that checkpoint before reconciliation
        // re-reads durable authority, as the conductor does between operations.
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.Reload();
        Assert.Equal(WorkTaskStatus.Failed, f.Task.Status);
        Assert.True(f.Task.CompletionBuildEvidence!.Applied);
        // Release custody through the owning workflow without weakening its monotonic-state guard.
        f.Coordinator().ReconcileAttached(f.Kernel, f.GoalId, f.TaskId,
            CompletionBuildEvidenceTarget.From(f.Task.CompletionBuildEvidence));
        Assert.True(f.Task.CompletionBuildEvidence.CustodyReleased);
        f.Kernel.RecordCompletionBuildEvidence(f.GoalId, f.TaskId, f.Task.CompletionBuildEvidence with
            { Diagnostic = CompletionBuildRelaunchRecoveryQualification.ChannelUnavailable });
        new SqliteOrchestratorStateRepository(f.StatePath).SaveAsync(f.Kernel).GetAwaiter().GetResult();
        f.FailLaunch = false;
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        return f;
    }

    internal static CompletionBuildRelaunchRecoveryQualification.Context Context(CompletionBuildEvidenceFixture f,
        Func<CompletionBuildEvidenceTarget, CompletionBuildEvidenceOperation> request) =>
        new(() => [f.Task.LastDispatch!.DispatchedAt.AddSeconds(1)], () => f.Clock.UtcNow, (_, _, target) => request(target));

    internal static ConductorAdvanceOutcome.Held? Decide(CompletionBuildEvidenceFixture f,
        CompletionBuildRelaunchRecoveryQualification.Context? context = null) =>
        WorkerBuildEvidenceRecoveryHold.Decide(f.Goal, GoalLifecycleState.Failed,
            new FailedGoalRecoveryIdentity(f.GoalId, f.TaskId, null, "fixture"), context);
}
