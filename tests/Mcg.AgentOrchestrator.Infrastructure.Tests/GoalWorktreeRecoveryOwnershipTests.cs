using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeRecoveryOwnershipTests : GoalWorktreeRecoveryTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("gate")]
    [Xunit.InlineData("unreadable-gate")]
    [Xunit.InlineData("dispatch")]
    [Xunit.InlineData("completion-build")]
    [Xunit.InlineData("inactive")]
    public void OwnedOrUncertainWorkspaceIsNotRecreated(string owner)
    {
        using var f = new MissingFixture();
        var task = f.Goal.Tasks.Single();
        var at = DateTimeOffset.UtcNow;
        var process = new TaskProcessRecord(12345, "fixture", f.Path, "out", "err", "exit", at, null, null);
        if (owner is "dispatch" or "completion-build")
            f.Kernel.RecordTaskDispatch(f.Goal.Id, task.Id, new("codex-cli", "fixture", f.Path, at));
        if (owner == "dispatch")
            f.Kernel.RecordTaskProcessStarted(f.Goal.Id, task.Id, process);
        if (owner == "completion-build")
        {
            f.Kernel.RecordCompletionBuildEvidence(f.Goal.Id, task.Id, new("build", f.Goal.Id.Value, task.Id.Value, at, "dispatch", f.Path,
                "tree", [], process, at.AddHours(1), CompletionBuildEvidencePhase.Pending, "state", f.Repo,
                "result", 12345, at));
            Assert.Null(task.LastProcess); // Only completion-build custody can refuse this case.
            Assert.True(task.CompletionBuildEvidence!.IsPending);
            Assert.False(task.CompletionBuildEvidence.CustodyReleased);
        }
        if (owner == "inactive") f.Kernel.CancelGoal(f.Goal.Id, "fixture inactive");
        var before = GoalWorktreeRegistrationListing.RunGit(f.Repo).Output;
        var coordinator = f.Coordinator();
        var refusal = Assert.ThrowsAny<InvalidOperationException>(() => ConductorMissingWorktreeRecovery.Prepare(
            f.Repo, f.Goal, coordinator, () => f.Kernel.Goals.ToArray(), _ => owner switch
            {
                "gate" => true,
                "unreadable-gate" => throw new InvalidOperationException("gate records unreadable"),
                _ => false
            }));
        if (owner == "completion-build")
            Assert.Contains("dispatch or completion-build owner holds workspace", refusal.Message);
        Assert.False(Directory.Exists(f.Path));
        Assert.Equal(before, GoalWorktreeRegistrationListing.RunGit(f.Repo).Output);
        f.AssertTip();
        var failure = Assert.Single(GoalOperationJournal.Read(f.Repo, f.Goal.Id).Entries.Where(e =>
            e.Operation == ConductorMissingWorktreeRecovery.Operation && e.Status == GoalOperationStatus.Failed));
        Assert.Contains(f.Path, failure.Detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PendingGateClaimOwnsWorkspaceDespitePublishedResult(bool claimed)
    {
        using var f = new MissingFixture();
        var metadata = Path.Combine(f.Repo, "attempt.json");
        var attempt = new ConductorGroupedGateAttempt("pending", "cohort", [], "main", "tree", "manifest", "identity",
            DateTimeOffset.UtcNow, 12345, 67890, metadata, Path.Combine(f.Repo, "result.json"),
            Path.Combine(f.Repo, "exit"), Path.Combine(f.Repo, "out"), Path.Combine(f.Repo, "err"), f.Repo, "{}") { HolderContractVersion = 1 };
        var binding = AcceptanceHolderBinding.For(attempt);
        var lease = new AcceptanceHolderLease(classify: _ => SpawnTrackedProcessStatus.LiveMatch,
            job: (_, _, _, _) => new AcceptanceJobObservation(true, false, "fixture tree unsettled"),
            attach: _ => new EmptyDisposable());
        var generation = AcceptanceHolderLease.CurrentGeneration();
        lease.Reserve(binding, generation);
        using var claim = claimed ? lease.Claim(binding, generation) : null;
        File.WriteAllText(attempt.ResultPath, "{\"Status\":\"completed\"}");
        File.WriteAllText(attempt.ExitCodePath, "0");
        var coordinator = new ConductorGroupedGateAttemptCoordinator(Path.Combine(f.Repo, "attempts"), holderLease: lease);
        Assert.True(coordinator.OwnsWorkspaceReadOnly(attempt));
        Assert.False(coordinator.ReservesCapacityReadOnly(attempt));
        // A terminal-looking outcome is not proof that the pending claim has settled.
        Assert.True(coordinator.OwnsWorkspaceReadOnly(attempt with { Outcome = "Passed", ReconciledAt = DateTimeOffset.UtcNow }));
        var result = f.Recover(() => coordinator.OwnsWorkspaceReadOnly(attempt) ? "gate owner holds workspace" : null);
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(f.Path));
        f.AssertTip();
        if (!claimed)
        {
            lease.CancelUnstartedLaunch(binding, generation);
            Assert.False(coordinator.OwnsWorkspaceReadOnly(attempt));
            Assert.True(f.Recover(() => coordinator.OwnsWorkspaceReadOnly(attempt) ? "gate owner holds workspace" : null).Succeeded);
            f.AssertRestored();
        }
    }

    [Xunit.Fact]
    public void UnknownLegacyHolderIsRetainedAfterTerminalOutcome()
    {
        using var f = new MissingFixture();
        var attempt = new ConductorGroupedGateAttempt("unknown", "cohort", [], "main", "tree", "manifest", "identity",
            DateTimeOffset.UtcNow, 12345, 67890, Path.Combine(f.Repo, "attempt.json"), Path.Combine(f.Repo, "result"),
            Path.Combine(f.Repo, "exit"), Path.Combine(f.Repo, "out"), Path.Combine(f.Repo, "err"),
            f.Repo, "{}", Outcome: "Passed", ReconciledAt: DateTimeOffset.UtcNow);
        var coordinator = new ConductorGroupedGateAttemptCoordinator(Path.Combine(f.Repo, "attempts"),
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown);
        Assert.True(coordinator.OwnsWorkspaceReadOnly(attempt));
        Assert.False(f.Recover(() => coordinator.OwnsWorkspaceReadOnly(attempt) ? "unsettled holder" : null).Succeeded);
        f.AssertTip();
    }

    [Xunit.Fact]
    public void PreparationLeaseCannotBeReclaimedAfterRecoveryReceipt()
    {
        using var f = new MissingFixture();
        var coordinator = f.Coordinator();
        using var ownership = ConductorMissingWorktreeRecovery.Prepare(f.Repo, f.Goal, coordinator,
            () => f.Kernel.Goals.ToArray(), _ => false);
        Assert.NotNull(ownership);
        var overlap = coordinator.TryBegin(f.Goal, "competing-operation");
        using var unexpectedScope = overlap.Scope;
        Assert.False(overlap.Acquired);
        Assert.Equal(GoalEvidenceLeaseRecoveryStatus.Live, overlap.LeaseFact?.RecoveryStatus);
        f.AssertRestored();
        ownership.Complete("fixture preparation finished");
    }

    [Xunit.Fact]
    public void RestartValidatesDirectoryLeftWithoutRecoveryReceipt()
    {
        using var f = new MissingFixture();
        GoalOperationJournal.Begin(f.Repo, f.Goal, "conductor:workspace-preparation", operationInstanceId: "interrupted");
        Assert.True(f.Recover().Succeeded); // Git completed, but no application recovery receipt was persisted.
        GoalOperationJournal.Aborted(f.Repo, f.Goal, "conductor:workspace-preparation", operationInstanceId: "interrupted");
        using var ownership = ConductorMissingWorktreeRecovery.Prepare(f.Repo, f.Goal, f.Coordinator(),
            () => f.Kernel.Goals.ToArray(), _ => false);
        Assert.NotNull(ownership);
        var receipt = Assert.Single(GoalOperationJournal.Read(f.Repo, f.Goal.Id).Entries.Where(entry =>
            entry.Operation == ConductorMissingWorktreeRecovery.Operation && entry.Status == GoalOperationStatus.Completed));
        Assert.Contains("recreated=False", receipt.Detail);
        Assert.Contains(f.Tip, receipt.Detail);
        f.AssertRestored();
        ownership.Complete("fixture preparation finished");
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    [Xunit.Fact]
    public void CollidingGoalPrefixCannotAuthorizeRecovery()
    {
        using var f = new MissingFixture();
        var suffix = f.Goal.Id.Value[8..];
        var differentSuffix = (suffix[0] == 'a' ? "b" : "a") + suffix[1..];
        f.Kernel.CreateGoal(new GoalId(f.Goal.Id.Value[..8] + differentSuffix), "Colliding goal");
        var result = f.Recover(() => ConductorMissingWorktreeRecovery.OwnerRefusal(f.Goal.Id, f.Kernel.Goals.ToArray(), _ => false));
        Assert.False(result.Succeeded);
        Assert.Contains("another goal", result.Observation);
        f.AssertTip();
        Assert.False(Directory.Exists(f.Path));
    }

    [Xunit.Theory]
    [Xunit.InlineData("acceptance-gate-attempts")]
    [Xunit.InlineData("pre-review-evidence-attempts")]
    public void UnreadableAttemptRecordsCannotProveIdle(string rootName)
    {
        using var f = new MissingFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Repo, f.Repo);
        var root = Path.Combine(workspace.OrchestratorDirectory, rootName, f.Goal.Id.Value);
        Directory.CreateDirectory(root);
        var record = Path.Combine(root, "corrupt.attempt.json");
        File.WriteAllText(record, "{malformed");
        var predicate = GoalLiveAcceptancePredicate.ForWorkspace(workspace, requireWorkspaceSettlement: true);
        var result = f.Recover(() => predicate(f.Goal.Id) ? "gate owner" : null);
        Assert.False(result.Succeeded);
        Assert.Contains("idle state could not be established", result.Observation);
        Assert.Equal("{malformed", File.ReadAllText(record));
        Assert.False(Directory.Exists(f.Path));
        f.AssertTip();
    }
}
