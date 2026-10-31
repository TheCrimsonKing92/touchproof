using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestLandingDeliveryTests
{
    [Fact]
    public void Cli_land_requests_save_and_reloaded_delivery_suppresses_forge_calls()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        Assert.Null(repository.LoadAsync().GetAwaiter().GetResult().GetGoal(fixture.Goal.Id).DeliveredForReview);
        Assert.Equal(AcceptanceEngineHealth.Healthy, PostLandingCanaryFactory.CreateCircuit(fixture.Workspace).Read().Health);
        var context = CliContext(fixture, fixture.Kernel);

        var shouldSave = CliCommandHandlers.Execute(["land", fixture.Goal.Id.Value[..8]], context);
        // This is the handler contract consumed by CliPersistentStateRunner.
        if (shouldSave) repository.SaveAsync(context.Kernel).GetAwaiter().GetResult();

        Assert.True(shouldSave);
        var restored = repository.LoadAsync().GetAwaiter().GetResult();
        var goal = restored.GetGoal(fixture.Goal.Id);
        var delivery = Assert.IsType<GoalDeliveredForReview>(goal.DeliveredForReview);
        Assert.Equal(fixture.Candidate, delivery.PushedCommit);
        Assert.Equal("delivered for review: " + PullRequestLandingTestFixture.Url, goal.CurrentHold!.Blocker);
        Assert.Contains(goal.Timeline, item => item.Message == goal.CurrentHold.Blocker);
        Assert.Equal(1, fixture.Forge.Creates);
        var calls = fixture.Forge.Calls.Count;

        Assert.False(CliCommandHandlers.Execute(["land", goal.Id.Value[..8]], CliContext(fixture, restored)));

        Assert.Equal(calls, fixture.Forge.Calls.Count);
        Assert.Equal(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "main"));
        Assert.Null(repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Cli_land_failure_requests_save_of_pushed_commit_provenance()
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Forge.Failure = "lookup";
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        Assert.Equal(AcceptanceEngineHealth.Healthy, PostLandingCanaryFactory.CreateCircuit(fixture.Workspace).Read().Health);
        var context = CliContext(fixture, fixture.Kernel);

        var shouldSave = CliCommandHandlers.Execute(["land", fixture.Goal.Id.Value[..8]], context);
        if (shouldSave) repository.SaveAsync(context.Kernel).GetAwaiter().GetResult();

        Assert.True(shouldSave);
        Assert.Single(fixture.Forge.Calls.Where(call => call.Command == "git" && call.Arguments[0] == "push"));
        var goal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(fixture.Goal.Id);
        Assert.Null(goal.DeliveredForReview);
        Assert.Contains(goal.Timeline, item => item.Kind == ProgressKind.GoalPolicyDecision &&
            item.Message.Contains($"pushed branch {GoalWorktreeLayout.BranchName(goal.Id)} commit {fixture.Candidate}"));
    }

    private static CliExecutionContext CliContext(PullRequestLandingTestFixture fixture, AgentOrchestratorKernel kernel) =>
        new(kernel, fixture.Workspace, new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(), kernel.GetGoal(fixture.Goal.Id))
        {
            PullRequestPublisher = new GitHubPullRequestPublisher(fixture.Forge)
        };

    [Fact]
    public void Solo_delivery_publishes_gate_candidate_and_holds_without_promoting_or_writing_refs()
    {
        using var fixture = new PullRequestLandingTestFixture();
        InfrastructureTestSupport.RunGitWithCommitPostcondition(fixture.Repository, ["branch", "integration", fixture.Main]);

        var evidence = PullRequestGateEvidenceResolver.Resolve(fixture.Workspace, fixture.Goal, fixture.Candidate);
        Assert.Equal(PullRequestLandingTestFixture.Attempt, evidence.AttemptId);
        Assert.Equal(new[] { "unit", "build" }, evidence.CheckNames);

        var result = fixture.Land();

        Assert.False(result.MainAdvanced);
        Assert.Equal("delivered for review: " + PullRequestLandingTestFixture.Url,
            Assert.IsType<LandingDecision.Escalate>(result.Decision).Reason);
        var delivery = Assert.IsType<GoalDeliveredForReview>(fixture.Goal.DeliveredForReview);
        Assert.Equal(7, delivery.PullRequestNumber);
        Assert.Equal(fixture.Candidate, delivery.PushedCommit);
        Assert.Equal(PullRequestLandingTestFixture.Attempt, delivery.GateAttemptId);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(result.Message, fixture.Goal.CurrentHold!.Blocker);
        Assert.Contains(fixture.Goal.Timeline, progress => progress.Message == result.Message);
        Assert.Equal(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "main"));
        Assert.Equal(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "integration"));
        var push = Assert.Single(fixture.Forge.Calls.Where(call => call.Command == "git" && call.Arguments[0] == "push"));
        Assert.Contains($"--force-with-lease=refs/heads/{GoalWorktreeLayout.BranchName(fixture.Goal.Id)}:", push.Arguments);
        Assert.Contains($"{fixture.Candidate}:refs/heads/{GoalWorktreeLayout.BranchName(fixture.Goal.Id)}", push.Arguments);
        Assert.Contains(fixture.Forge.Calls, call => call.Command == "gh" && call.Arguments[1] == "list");
        var create = Assert.Single(fixture.Forge.Calls.Where(call => call.Command == "gh" && call.Arguments[1] == "create"));
        Assert.Equal("main", create.Arguments[Array.IndexOf(create.Arguments, "--base") + 1]);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        Assert.Null(repository.ReadPromotionFactAsync(fixture.Goal.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Existing_pr_is_reused_and_matching_persisted_delivery_suppresses_all_forge_calls()
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Forge.ExistingPr = true;
        fixture.Land();
        Assert.Equal(7, fixture.Goal.DeliveredForReview!.PullRequestNumber);
        Assert.Equal(0, fixture.Forge.Creates);
        var calls = fixture.Forge.Calls.Count;
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()))!;
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var goal = restored.GetGoal(fixture.Goal.Id);

        var result = LandingExecutor.Execute(restored, goal, fixture.Workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge));

        Assert.Equal(calls, fixture.Forge.Calls.Count);
        Assert.Equal(fixture.Goal.DeliveredForReview, goal.DeliveredForReview);
        Assert.Equal("delivered for review: " + PullRequestLandingTestFixture.Url, result.Message);
    }

    [Fact]
    public void Lookup_failure_retains_push_provenance_and_retry_creates_only_one_pr()
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Forge.Failure = "lookup";
        var failed = fixture.Land();
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.Contains($"pushed branch {GoalWorktreeLayout.BranchName(fixture.Goal.Id)} commit {fixture.Candidate}", failed.Message);
        fixture.Forge.Failure = null;
        fixture.Land();
        Assert.Equal(1, fixture.Forge.Creates);
        Assert.Single(fixture.Forge.Calls.Where(call => call.Command == "git" && call.Arguments[0] == "push"));
        Assert.NotNull(fixture.Goal.DeliveredForReview);
    }

    [Fact]
    public void Direct_project_still_uses_trunk_promotion_without_calling_forge()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal,
            fixture.Workspace with { Delivery = ProjectDelivery.Direct },
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge));
        Assert.True(result.MainAdvanced, result.Message);
        Assert.IsType<LandingDecision.Promote>(result.Decision);
        Assert.Empty(fixture.Forge.Calls);
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.NotEqual(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "main"));
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        Assert.NotNull(repository.ReadPromotionFactAsync(fixture.Goal.Id).GetAwaiter().GetResult());
    }

    [Fact]
    public void Failed_delivery_event_write_rolls_back_record_hold_and_timeline_together()
    {
        using var fixture = new PullRequestLandingTestFixture();
        var before = fixture.Kernel.ExportSnapshot();
        var delivered = new GoalDeliveredForReview(7, PullRequestLandingTestFixture.Url, fixture.Candidate,
            PullRequestLandingTestFixture.Attempt, DateTimeOffset.UtcNow);
        fixture.Kernel.SetEventWriter(new FailDeliveryEventWriter());

        Assert.Throws<IOException>(() => fixture.Kernel.RecordGoalDeliveredForReview(fixture.Goal.Id, delivered));

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot()));
    }

    [Fact]
    public void Record_write_failure_holds_and_next_tick_reconciles_with_exactly_one_create()
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Kernel.SetEventWriter(new FailDeliveryEventWriter());
        var failed = fixture.Land();
        Assert.StartsWith("landing mutation blocked:", failed.Message);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.DoesNotContain(fixture.Goal.Timeline, progress => progress.Message.StartsWith("delivered for review:", StringComparison.Ordinal));

        fixture.Land();

        Assert.NotNull(fixture.Goal.DeliveredForReview);
        Assert.Equal(1, fixture.Forge.Creates);
        Assert.Single(fixture.Forge.Calls.Where(call => call.Command == "git" && call.Arguments[0] == "push"));
    }

    [Fact]
    public void Record_failure_preserves_lease_for_changed_candidate_after_reload()
    {
        using var fixture = new PullRequestLandingTestFixture();
        fixture.Kernel.SetEventWriter(new FailDeliveryEventWriter());
        var context = CliContext(fixture, fixture.Kernel);

        var shouldSave = CliCommandHandlers.Execute(["land", fixture.Goal.Id.Value[..8]], context);

        Assert.True(shouldSave);
        Assert.Null(fixture.Goal.DeliveredForReview);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        var repository = new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath);
        repository.SaveAsync(context.Kernel).GetAwaiter().GetResult();
        var restored = repository.LoadAsync().GetAwaiter().GetResult();
        var goal = restored.GetGoal(fixture.Goal.Id);
        Assert.Null(goal.DeliveredForReview);
        Assert.DoesNotContain(goal.Timeline, item => item.Message.StartsWith("delivered for review:", StringComparison.Ordinal));

        // Re-gate an actual new commit before retrying the interrupted local record write.
        var branch = GoalWorktreeLayout.BranchName(goal.Id);
        var worktree = GoalWorktrees.WorktreePath(fixture.Repository, goal.Id);
        LandingExecutorTests.AppendCommit(worktree, "src/delivery.txt", "updated candidate");
        var candidate = LandingExecutorTests.ReadGit(worktree, "rev-parse", "HEAD");
        Assert.NotEqual(fixture.Candidate, candidate);
        var metadataPath = Path.Combine(fixture.Workspace.OrchestratorDirectory,
            "acceptance-gate-attempts", goal.Id.Value, PullRequestLandingTestFixture.Attempt + ".attempt.json");
        File.WriteAllText(metadataPath, File.ReadAllText(metadataPath).Replace(fixture.Candidate, candidate, StringComparison.Ordinal));
        GoalOperationJournal.AcceptancePassed(fixture.Repository, goal, "conductor:acceptance", candidate,
            fixture.Main, "passing acceptance for the updated candidate", DateTimeOffset.UtcNow);

        var result = LandingExecutor.Execute(restored, goal, fixture.Workspace,
            pullRequestPublisher: new GitHubPullRequestPublisher(fixture.Forge));

        Assert.Equal("delivered for review: " + PullRequestLandingTestFixture.Url, result.Message);
        Assert.Equal(candidate, goal.DeliveredForReview!.PushedCommit);
        var pushes = fixture.Forge.Calls.Where(call => call.Command == "git" && call.Arguments[0] == "push").ToArray();
        Assert.Equal(2, pushes.Length);
        Assert.Contains($"--force-with-lease=refs/heads/{branch}:{fixture.Candidate}", pushes[1].Arguments);
        Assert.Contains($"{candidate}:refs/heads/{branch}", pushes[1].Arguments);
        Assert.Equal(1, fixture.Forge.Creates);
        Assert.Equal(fixture.Main, LandingExecutorTests.ReadGit(fixture.Repository, "rev-parse", "main"));
        Assert.Null(repository.ReadPromotionFactAsync(goal.Id).GetAwaiter().GetResult());
    }

    private sealed class FailDeliveryEventWriter : IGoalLifecycleEventWriter
    {
        private bool _failed;
        public void AppendTimelineEvent(ProgressEvent progress)
        {
            if (!_failed && progress.Message.StartsWith("delivered for review:", StringComparison.Ordinal))
            { _failed = true; throw new IOException("record write failed"); }
        }
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> topics, string recoveryCommand) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }
        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) { }
        public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string hash) { }
        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
        public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
        public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) { }
        public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }
        public void AppendCleanedUp(GoalId goalId) { }
        public void AppendProgressiveReviewGlanceReceipt(GoalId goalId, TaskId taskId, string trigger, string inputsHash,
            string verdict, string note, int inputTokens, int outputTokens, int totalTokens, TimeSpan wallTime, string? model, string? profile) { }
        public void AppendProgressiveReviewGlanceSummary(GoalId goalId, int totalGlances, int onTrack, int concern,
            int fundamentalMisdirection, int invalid, int totalTokens) { }
    }
}
