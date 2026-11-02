using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

// Private repositories and bare origin; serialized for LandingExecutor's shared GitRunner seam.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestTrunkDriftTests : GoalWorktreeTestBase
{
    [Fact]
    public void Drift_releases_delivery_hold_and_projects_new_trunk_for_admission()
    {
        using var fixture = new Fixture();
        Assert.NotNull(fixture.Goal.CurrentHold);
        Assert.True(fixture.Coordinator.HasPassedReconciledReceipt(fixture.Goal.Id.Value));
        var trunk = fixture.AdvanceOrigin();
        Assert.Contains(fixture.Goal.Id, fixture.Tick());
        Assert.NotNull(fixture.Receipt.RegateRequestedAt);
        Assert.Equal(ConductorEvidenceSupersessionCause.CandidateChanged, fixture.Receipt.SupersessionCause);
        Assert.Contains("pull request refresh started:", fixture.Receipt.Detail);
        Assert.False(fixture.Coordinator.HasPassedReconciledReceipt(fixture.Goal.Id.Value));
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.True(PullRequestDeliveryTracker.IsEligible(fixture.Goal));
        var driver = new ConductorDriver(fixture.Kernel, fixture.Workspace, new NeverRunVerifier(),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
            cleanupHooks: CreateIsolatedCleanupContext(fixture.Root).Hooks);
        var ready = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
            driver.ProjectGateReadyCandidate(fixture.Goal, ConductorAutonomyPolicy.Permissive));
        Assert.Equal(trunk, ready.Projection.MergeEvidence.MainRevision);
        Assert.Equal(fixture.Pushed, ready.Projection.MergeEvidence.BranchRevision);
        Assert.Single(fixture.Publisher.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("writer")]
    [InlineData("candidate")]
    [InlineData("attempt")]
    [InlineData("unreconciled")]
    [InlineData("failed")]
    public void Unusable_receipt_or_writer_custody_preserves_delivery_hold(string fault)
    {
        using var fixture = new Fixture(denyReceiptWriter: fault == "writer");
        var receipt = fixture.Receipt;
        if (fault == "missing") File.Delete(receipt.MetadataPath);
        else
        {
            var unusable = fault switch
            {
                "candidate" => receipt with { BranchHeadSha = fixture.Git("rev-parse", "main") },
                "attempt" => receipt with { AttemptId = "different-gate" },
                "unreconciled" => receipt with { ReconciledAt = null },
                "failed" => receipt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed },
                _ => receipt
            };
            if (fault == "attempt") File.Delete(receipt.MetadataPath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(receipt.MetadataPath)!, unusable.AttemptId + ".attempt.json"),
                JsonSerializer.Serialize(unusable, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        var hold = fixture.Goal.CurrentHold;
        fixture.AdvanceOrigin();

        var output = AsyncLocalConsoleRouter.Capture(() => fixture.Tick());

        Assert.Equal(hold, fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix));
        Assert.Contains("unavailable: could not invalidate delivered gate receipt", output);
        if (fault != "missing") Assert.Null(fixture.Receipt.RegateRequestedAt);
        Assert.Single(fixture.Publisher.Requests);
        Assert.Equal(fixture.Pushed, fixture.RemoteGoal());
    }

    [Fact]
    public void Closed_without_refresh_allows_reopened_pr_to_refresh()
    {
        using var fixture = new Fixture();
        fixture.Reader.Status = new(PullRequestStatusKind.ClosedUnmerged);
        fixture.Tick();
        Assert.Single(fixture.Decisions(PullRequestLandingDelivery.ClosedUnmergedPrefix));
        Assert.False(PullRequestLandingDelivery.RefreshStopped(fixture.Goal));
        Assert.Empty(fixture.Decisions(PullRequestLandingDelivery.RefreshStoppedPrefix));
        var trunk = fixture.AdvanceOrigin();
        fixture.Reader.Status = new(PullRequestStatusKind.Open);
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        fixture.Tick();

        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Single(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix));
        var candidate = fixture.Integrate();
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: true);
        Assert.Contains("delivered for review:", fixture.Deliver().Message);
        Assert.Equal(2, fixture.Publisher.Requests.Count);
        Assert.Equal(fixture.Pushed, fixture.Publisher.Requests[1].PreviouslyPushedCommit);
        Assert.Equal(candidate, fixture.RemoteGoal());
    }

    [Fact]
    public void Closed_without_refresh_allows_later_gated_redelivery()
    {
        using var fixture = new Fixture();
        fixture.Reader.Status = new(PullRequestStatusKind.ClosedUnmerged);
        fixture.Tick();
        var trunk = fixture.AdvanceOrigin();
        fixture.Git("merge", "--ff-only", trunk);
        var candidate = fixture.Integrate();
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: true);

        Assert.Contains("delivered for review:", fixture.Deliver().Message);

        Assert.Equal(2, fixture.Publisher.Requests.Count);
        Assert.Equal(fixture.Pushed, fixture.Publisher.Requests[1].PreviouslyPushedCommit);
        Assert.Equal(candidate, fixture.RemoteGoal());
        Assert.Empty(fixture.Decisions("pull request refresh"));
    }

    [Fact]
    public void Open_without_drift_preserves_delivery_and_throttles_fetch()
    {
        using var fixture = new Fixture();
        var delivery = fixture.Goal.DeliveredForReview;
        var hold = fixture.Goal.CurrentHold;
        var main = fixture.Git("rev-parse", "main");
        Assert.Empty(fixture.Tick());
        fixture.Elapsed = TimeSpan.FromSeconds(119);
        Assert.Empty(fixture.Tick());
        Assert.Equal(1, fixture.Runner.Fetches);
        Assert.Equal(1, fixture.Reader.Reads);
        Assert.Equal(main, fixture.Git("rev-parse", "main"));
        Assert.Same(delivery, fixture.Goal.DeliveredForReview);
        Assert.Equal(hold, fixture.Goal.CurrentHold);
        Assert.Single(fixture.Publisher.Requests);
        Assert.Empty(fixture.Decisions("pull request refresh"));
    }

    [Fact]
    public void Drift_pass_republishes_same_branch_with_previous_commit_lease()
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        fixture.Tick();
        Assert.Equal(trunk, fixture.Git("rev-parse", "main"));
        Assert.Equal(trunk, fixture.Git("rev-parse", "HEAD"));
        Assert.Equal("", fixture.Git("status", "--porcelain", "--untracked-files=no"));
        Assert.Equal($"pull request refresh started: {PullRequestLandingTestFixture.Url} trunk={trunk} previous={fixture.Pushed}",
            Assert.Single(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix)));
        // Exercise conductor's integration and receipt-consuming delivery seams, without a live attempt.
        var candidate = fixture.Integrate();
        Assert.NotEqual(fixture.Pushed, candidate);
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: true);
        var result = fixture.Deliver();
        Assert.Contains("delivered for review:", result.Message);
        Assert.Equal(2, fixture.Publisher.Requests.Count);
        var request = fixture.Publisher.Requests[1];
        Assert.Equal(fixture.Pushed, request.PreviouslyPushedCommit);
        Assert.Equal(fixture.Publisher.Requests[0].GoalBranch, request.GoalBranch);
        Assert.Equal(candidate, request.CandidateCommit);
        Assert.Equal(candidate, fixture.RemoteGoal());
        Assert.Equal(7, fixture.Goal.DeliveredForReview!.PullRequestNumber);
        Assert.Equal(candidate, fixture.Goal.DeliveredForReview.PushedCommit);
        Assert.Equal($"pull request refreshed: {PullRequestLandingTestFixture.Url} pushed={candidate} previous={fixture.Pushed}",
            Assert.Single(fixture.Decisions("pull request refreshed: ")));
        Assert.False(PullRequestLandingDelivery.HasRefreshStarted(fixture.Goal));
    }

    [Fact]
    public void Failed_refresh_gate_keeps_previous_remote_commit_without_retry()
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        fixture.Tick();
        var candidate = fixture.Integrate();
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: false);
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(fixture.Goal.Id, "refresh gate started"));
        Assert.True(fixture.Kernel.ReconcileGoalAcceptanceFailed(fixture.Goal.Id, ["unit"],
            "refresh gate failed", candidate, trunk));
        fixture.Deliver();
        Assert.Equal(GoalStatus.AcceptanceFailed, fixture.Goal.Status);
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        Assert.Single(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix));
        Assert.Empty(fixture.Decisions("pull request refreshed: "));
        Assert.Single(fixture.Publisher.Requests);
        Assert.Equal(fixture.Pushed, fixture.RemoteGoal());
        Assert.Equal(fixture.Pushed, fixture.Goal.DeliveredForReview!.PushedCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Divergent_or_guarded_local_trunk_keeps_hold_and_raises_reconcile(bool dirty)
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        if (dirty) File.AppendAllText(Path.Combine(fixture.Root, "README.md"), "dirty");
        else AppendCommit(fixture.Root, "local-only.txt", "local divergence");
        var local = fixture.Git("rev-parse", "main");
        fixture.Tick();
        Assert.Equal(local, fixture.Git("rev-parse", "main"));
        Assert.NotEqual(trunk, local);
        Assert.NotNull(fixture.Goal.CurrentHold);
        Assert.Empty(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix));
        var item = Assert.Single(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => item.CorrelationKey?.StartsWith("pull-request-reconcile:") == true));
        Assert.Contains(local, item.Body);
        Assert.Contains(trunk, item.Body);
        Assert.Single(fixture.Publisher.Requests);
    }

    [Fact]
    public void Active_refresh_keeps_polling_and_coalesces_additional_trunk_movement()
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        fixture.Tick();
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(fixture.Goal.Id, "refresh gate started"));
        fixture.AdvanceOrigin();
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Equal(GoalStatus.Verifying, fixture.Goal.Status);
        Assert.Equal(1, fixture.Runner.Fetches);
        Assert.Equal(trunk, fixture.Git("rev-parse", "main"));
        Assert.Single(fixture.Decisions(PullRequestLandingDelivery.RefreshStartedPrefix));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Terminal_status_during_refresh_prevents_queued_publication(bool merged)
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        fixture.Tick();
        var candidate = fixture.Integrate();
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: true);
        Assert.True(fixture.Kernel.BeginGoalAcceptanceVerification(fixture.Goal.Id, "refresh gate started"));
        string? merge = null;
        if (merged)
        {
            fixture.Git("checkout", "-b", "forge-merge", "refs/remotes/origin/main");
            fixture.Git("merge", "--no-ff", fixture.Pushed, "-m", "Forge merge");
            merge = fixture.Git("rev-parse", "HEAD");
            fixture.Git("push", "origin", "HEAD:main");
            fixture.Git("checkout", "main");
        }
        fixture.Reader.Status = new(merged ? PullRequestStatusKind.Merged : PullRequestStatusKind.ClosedUnmerged,
            MergeCommit: merge, Url: PullRequestLandingTestFixture.Url);
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        if (merged)
        {
            Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
            Assert.Equal(merge, fixture.Git("rev-parse", "main"));
        }
        else
        {
            Assert.Single(fixture.Decisions(PullRequestLandingDelivery.ClosedUnmergedPrefix));
            Assert.Equal(PullRequestLandingDelivery.RefreshStoppedPrefix + "closed unmerged " + PullRequestLandingTestFixture.Url,
                Assert.Single(fixture.Decisions(PullRequestLandingDelivery.RefreshStoppedPrefix)));
        }
        // Previously queued publication must recheck the terminal observation.
        var result = PullRequestLandingDelivery.Deliver(fixture.Kernel, fixture.Goal, fixture.Workspace,
            candidate, fixture.Publisher, null, null, null, new SystemClock());
        Assert.Contains("refresh stopped", result.Message);
        Assert.Single(fixture.Publisher.Requests);
        Assert.Equal(fixture.Pushed, fixture.RemoteGoal());
        Assert.Empty(fixture.Decisions("pull request refreshed: "));
    }

    [Fact]
    public void Conflicting_refresh_preserves_previous_remote_commit()
    {
        using var fixture = new Fixture();
        fixture.AdvanceOrigin("src/delivery.txt");
        fixture.Tick();
        Assert.Null(fixture.Goal.CurrentHold);
        var integration = GoalWorktrees.TryRebaseOntoMain(fixture.Root, fixture.Goal.Id, trunkBranch: "main");
        Assert.Equal(GoalWorktreeRebaseStatus.Conflict, integration.Status);
        Assert.Contains("src/delivery.txt", integration.ConflictFiles);
        Assert.Single(fixture.Publisher.Requests);
        Assert.Equal(fixture.Pushed, fixture.RemoteGoal());
        Assert.Equal(fixture.Pushed, fixture.Goal.DeliveredForReview!.PushedCommit);
        Assert.Empty(fixture.Decisions("pull request refreshed: "));
    }

    [Fact]
    public void Rejected_refresh_publication_preserves_delivery_and_records_no_success()
    {
        using var fixture = new Fixture();
        var trunk = fixture.AdvanceOrigin();
        fixture.Tick();
        var candidate = fixture.Integrate();
        WriteReceipt(fixture.Workspace, fixture.Goal, candidate, trunk, passed: true);
        fixture.Publisher.Reject = true;
        var result = fixture.Deliver();
        Assert.Contains("pull request delivery failed: push-rejected:", result.Message);
        Assert.Equal(2, fixture.Publisher.Requests.Count);
        Assert.Equal(fixture.Pushed, fixture.Publisher.Requests[1].PreviouslyPushedCommit);
        Assert.Equal(fixture.Pushed, fixture.RemoteGoal());
        Assert.Equal(fixture.Pushed, fixture.Goal.DeliveredForReview!.PushedCommit);
        Assert.Single(fixture.Decisions("pull request delivery failed: "));
        Assert.Empty(fixture.Decisions("pull request refreshed: "));
    }

    [Fact]
    public void Direct_delivery_project_does_not_poll_fetch_or_refresh()
    {
        using var fixture = new Fixture();
        fixture.AdvanceOrigin();
        var before = fixture.Git("rev-parse", "main");
        var tracker = fixture.CreateTracker(fixture.Workspace with { Delivery = ProjectDelivery.Direct });
        Assert.Empty(tracker.Service([fixture.Goal]));
        Assert.Equal(0, fixture.Reader.Reads);
        Assert.Equal(0, fixture.Runner.Fetches);
        Assert.Equal(before, fixture.Git("rev-parse", "main"));
        Assert.NotNull(fixture.Goal.CurrentHold);
        Assert.Single(fixture.Publisher.Requests);
    }

    private static void WriteReceipt(OrchestratorWorkspace workspace, Goal goal, string candidate, string main, bool passed)
    {
        const string attempt = "refresh-gate-attempt";
        var directory = Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts", goal.Id.Value);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, attempt + ".attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = attempt, goalId = goal.Id.Value, branchHeadSha = candidate,
            kind = "gate", startedAt = DateTimeOffset.UtcNow, executionProtocol = "in-process"
        }));
        var verification = new AcceptanceVerificationResult(passed, false, passed ? 0 : 1, passed ? null : "unit failed",
            Checks: [new AcceptanceCheckResult("unit", passed, passed ? 0 : 1, null),
                new AcceptanceCheckResult("build", true, 0, null)]) { AttemptId = attempt };
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [], [], candidate, main, null, null, null);
        var artifact = new ConductorParallelAcceptanceRunArtifact(passed ? "accepted" : "failed", null, null, null, null,
            summary, candidate, main, null, null);
        File.WriteAllText(Path.Combine(directory, attempt + ".result.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (passed) GoalOperationJournal.AcceptancePassed(workspace.ExecutionDirectory, goal, "conductor:acceptance",
            candidate, main, "passing refresh gate", DateTimeOffset.UtcNow);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PullRequestLandingTestFixture _delivery = new();
        private readonly string _origin = Directory.CreateTempSubdirectory("mcg-pr-drift-origin-").FullName;
        private int _advances;
        internal string Root => _delivery.Repository;
        internal OrchestratorWorkspace Workspace => _delivery.Workspace;
        internal AgentOrchestratorKernel Kernel => _delivery.Kernel;
        internal Goal Goal => _delivery.Goal;
        internal string Pushed => _delivery.Candidate;
        internal Publisher Publisher { get; } = new();
        internal StatusReader Reader { get; } = new();
        internal FetchRunner Runner { get; } = new();
        internal TimeSpan Elapsed;
        internal CollaborationItemStore Attention { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal ConductorParallelAcceptanceAttempt Receipt => Coordinator.TryReadLatest(Goal.Id.Value)!;
        private readonly PullRequestDeliveryTracker _tracker;

        internal Fixture(bool denyReceiptWriter = false)
        {
            RunGit(_origin, "init", "--bare");
            Git("remote", "add", "origin", _origin);
            Git("push", "origin", "main");
            new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
            Deliver();
            Assert.Single(Publisher.Requests);
            Assert.Equal(Pushed, Goal.DeliveredForReview!.PushedCommit);
            Assert.Equal(Pushed, RemoteGoal());
            var attemptRoot = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts");
            var directory = Path.Combine(attemptRoot, Goal.Id.Value);
            var metadata = Path.Combine(directory, PullRequestLandingTestFixture.Attempt + ".attempt.json");
            var result = Path.Combine(directory, PullRequestLandingTestFixture.Attempt + ".result.json");
            var now = DateTimeOffset.UtcNow;
            var receipt = new ConductorParallelAcceptanceAttempt(PullRequestLandingTestFixture.Attempt,
                Goal.Id.Value, Goal.Id.Value[..8], 0, Pushed, _delivery.Main, now, now, 0,
                ConductorParallelAcceptanceAttemptOutcome.Passed, "", "", "", "", result, metadata,
                ScopePaths: ["src/delivery.txt"], CompletedAt: now, ReconciledAt: now, ExecutionProtocol: "in-process")
            {
                ReplayContextFingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(Goal)
            };
            File.WriteAllText(metadata, JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Coordinator = new(attemptRoot, "main", executionDirectory: Root,
                tryAcquireAttemptWriterLease: denyReceiptWriter ? (_, _) => null : null);
            Attention = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            _tracker = CreateTracker();
        }

        internal PullRequestDeliveryTracker CreateTracker(OrchestratorWorkspace? workspace = null) =>
            new(Kernel, workspace ?? Workspace, Reader, Runner, Attention, elapsed: () => Elapsed,
                acceptanceAttempts: Coordinator);
        internal IReadOnlySet<GoalId> Tick() => _tracker.Service([Goal]);
        internal LandingResult Deliver() => LandingExecutor.Execute(Kernel, Goal, Workspace, pullRequestPublisher: Publisher);
        internal string Git(params string[] args) => ReadGit(Root, args);
        internal string RemoteGoal() => ReadGit(_origin, "rev-parse", "refs/heads/" + GoalWorktreeLayout.BranchName(Goal.Id));
        internal string[] Decisions(string prefix) => Goal.Timeline.Where(item => item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.StartsWith(prefix, StringComparison.Ordinal)).Select(item => item.Message).ToArray();
        internal string AdvanceOrigin(string? path = null)
        {
            Git("checkout", "-b", "remote-change-" + ++_advances, "refs/remotes/origin/main");
            AppendCommit(Root, path ?? "trunk-" + _advances + ".txt", "unrelated trunk work");
            var trunk = Git("rev-parse", "HEAD");
            Git("push", "origin", "HEAD:main");
            Git("checkout", "main");
            return trunk;
        }
        internal string Integrate()
        {
            var integrated = GoalWorktrees.TryRebaseOntoMain(Root, Goal.Id, trunkBranch: "main");
            Assert.Equal(GoalWorktreeRebaseStatus.Rebased, integrated.Status);
            return Git("rev-parse", GoalWorktreeLayout.BranchName(Goal.Id));
        }
        public void Dispose() { _delivery.Dispose(); TryDeleteDirectory(_origin); }
    }

    private static void RunGit(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, result.Error);
    }

    private sealed class Publisher : IPullRequestPublisher
    {
        internal List<PullRequestPublishRequest> Requests { get; } = [];
        internal bool Reject;
        public PullRequestPublishResult Publish(PullRequestPublishRequest request)
        {
            Requests.Add(request);
            if (Reject) return new(null, null, null, PullRequestPublishFailureKind.PushRejected, "lease rejected");
            RunGit(request.RepositoryRoot, "push", "--force-with-lease=refs/heads/" + request.GoalBranch + ":" + request.PreviouslyPushedCommit,
                "origin", request.CandidateCommit + ":refs/heads/" + request.GoalBranch);
            return new(7, PullRequestLandingTestFixture.Url, request.CandidateCommit);
        }
    }

    private sealed class StatusReader : IPullRequestStatusReader
    {
        internal int Reads;
        internal PullRequestStatus Status = new(PullRequestStatusKind.Open, Url: PullRequestLandingTestFixture.Url);
        public PullRequestStatus Read(string repositoryRoot, int number) { Reads++; return Status; }
    }

    private sealed class FetchRunner : IForgeProcessRunner
    {
        private readonly ForgeProcessRunner _inner = new();
        internal int Fetches;
        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            Assert.Equal("git", fileName);
            Assert.Equal("fetch", arguments[0]);
            Fetches++;
            return _inner.Run(fileName, arguments, workingDirectory, environment, timeout);
        }
    }

    private sealed class NeverRunVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(string worktreePath, GoalId? goalId,
            IReadOnlyList<string>? changedFiles, int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease, IAcceptanceAttemptExecutionOwner executionOwner) =>
            throw new InvalidOperationException("Admission projection must not execute a gate.");
        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(string worktreePath, GoalId? goalId,
            string request, IAcceptanceFocusedVerificationOwner executionOwner, int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null, bool runBaselineArm = false) =>
            throw new InvalidOperationException("Admission projection must not execute focused evidence.");
    }
}
