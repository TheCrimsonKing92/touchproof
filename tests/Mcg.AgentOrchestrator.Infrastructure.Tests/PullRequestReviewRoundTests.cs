using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: repositories and stores are private; GitRunner overrides are flow-local.
public sealed class PullRequestReviewRoundTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    private const string Url = PullRequestLandingTestFixture.Url;

    [Fact]
    public void Recorded_batch_routes_developer_once_with_each_review_and_contract()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, ("zoe", submitted, "Keep the empty-input case."),
            ("alice", submitted.AddMinutes(1), "Fix the invalid result.\nPreserve the details."));
        var criteria = fixture.Goal.EffectiveAcceptanceCriteriaCorrections.ToArray();

        fixture.Tick();
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();

        var developer = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer);
        var retry = Assert.Single(fixture.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(developer.Id, retry.TaskId);
        var feedback = Assert.IsType<AcceptedRetryFeedback>(developer.AcceptedRetryFeedback);
        Assert.Equal(CriteriaCorrectionSource.Escalation, feedback.Source);
        Assert.Contains("zoe", feedback.Message);
        Assert.Contains("alice", feedback.Message);
        Assert.Contains(Stamp(submitted), feedback.Message);
        Assert.Contains(Stamp(submitted.AddMinutes(1)), feedback.Message);
        Assert.Contains("Keep the empty-input case.", feedback.Message);
        Assert.Contains("Fix the invalid result.\nPreserve the details.", feedback.Message);
        Assert.Contains(PullRequestReviewRound.Instruction, feedback.Message);
        Assert.Equal(RetryCause.ContractClarification, developer.PendingRetryCause);
        Assert.Equal(criteria, fixture.Goal.EffectiveAcceptanceCriteriaCorrections);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
        Assert.Equal($"{PullRequestReviewRound.StartedPrefix}{Url} round=1 reviewers=alice,zoe",
            Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix)));
    }

    [Fact]
    public void Inflight_batches_survive_reload_and_push_then_merge_into_next_round()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, ("alice", submitted, "first request"));
        fixture.Tick();
        Open(fixture, ("alice", submitted, "first request"), ("bob", submitted.AddMinutes(1), "second request"));
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        Open(fixture, ("alice", submitted, "first request"), ("bob", submitted.AddMinutes(1), "second request"),
            ("zoe", submitted.AddMinutes(2), "third request"));
        fixture.Elapsed = TimeSpan.FromMinutes(4);
        fixture.Tick();
        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix));
        Assert.Single(fixture.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        var publisher = new Publisher();
        // Both pending batches precede the new push; the consumed batch's timestamp is the boundary.
        CompleteRound(fixture, publisher, deliveredAt: submitted.AddMinutes(10));
        fixture.Repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        var restored = fixture.Repository.LoadAsync().GetAwaiter().GetResult();
        var goal = restored.GetGoal(fixture.Goal.Id);
        var tracker = new PullRequestDeliveryTracker(restored, fixture.Workspace,
            new GitHubPullRequestStatusReader(fixture.Forge), fixture.Forge, fixture.Attention,
            elapsed: () => fixture.Elapsed, conflictCheck: fixture.ConflictCheck);

        tracker.Service([goal]);
        // Wait for the check completion event, then consume it on the next service pass.
        fixture.ConflictCheck.PendingTask(goal.Id)!.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        fixture.Elapsed += TimeSpan.FromMinutes(2);
        Assert.Contains(goal.Id, tracker.Service([goal]));

        Assert.Equal(2, Decisions(goal, PullRequestReviewRound.StartedPrefix).Length);
        Assert.Equal(2, goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried));
        var feedback = goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer).AcceptedRetryFeedback!.Message;
        Assert.Contains("bob", feedback);
        Assert.Contains("second request", feedback);
        Assert.Contains("zoe", feedback);
        Assert.Contains("third request", feedback);
        Assert.DoesNotContain("first request", feedback);
        Assert.Equal($"{PullRequestReviewRound.StartedPrefix}{Url} round=2 reviewers=bob,zoe",
            Decisions(goal, PullRequestReviewRound.StartedPrefix)[1]);
    }

    [Fact]
    public void Passed_round_republishes_same_branch_with_prior_commit_lease()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var previous = fixture.Goal.DeliveredForReview!.PushedCommit;
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview.DeliveredAt.AddMinutes(1), "fix"));
        fixture.Tick();
        var publisher = new Publisher();

        var candidate = CompleteRound(fixture, publisher);

        var request = Assert.Single(publisher.Requests);
        Assert.Equal(previous, request.PreviouslyPushedCommit);
        Assert.Equal(GoalWorktreeLayout.BranchName(fixture.Goal.Id), request.GoalBranch);
        Assert.NotEqual(previous, request.CandidateCommit);
        Assert.Equal(candidate, request.CandidateCommit);
        Assert.Equal(candidate, fixture.Goal.DeliveredForReview!.PushedCommit);
        Assert.Equal(7, fixture.Goal.DeliveredForReview.PullRequestNumber);
        Assert.Equal($"{PullRequestReviewRound.DeliveredPrefix}{Url} round=1 pushed={candidate}",
            Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.DeliveredPrefix)));
        Assert.False(PullRequestReviewRound.InFlight(fixture.Goal));
        Assert.Equal("delivered for review: " + Url, fixture.Goal.CurrentHold!.Blocker);
    }

    [Fact]
    public void Fourth_batch_after_three_delivered_rounds_raises_one_decider_item()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var workspace = fixture.Workspace with { DeliveryDecider = "project-lead" };
        var tracker = fixture.CreateTracker(workspace);
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt;
        var publisher = new Publisher();
        for (var round = 1; round <= 3; round++)
        {
            Open(fixture, ("alice", submitted.AddMinutes(round), "request " + round));
            fixture.Elapsed = TimeSpan.FromMinutes(round * 2);
            tracker.Service([fixture.Goal]);
            Assert.Equal(round, Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix).Length);
            CompleteRound(fixture, publisher);
        }
        Assert.Equal(3, publisher.Requests.Count);
        Open(fixture, ("pending-reviewer", submitted.AddMinutes(4), "fourth request"));
        fixture.Elapsed = TimeSpan.FromMinutes(8);

        tracker.Service([fixture.Goal]);
        fixture.Elapsed = TimeSpan.FromMinutes(10);
        tracker.Service([fixture.Goal]);

        Assert.Equal(3, Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix).Length);
        Assert.Equal(3, fixture.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(3, Decisions(fixture.Goal, PullRequestReviewRound.DeliveredPrefix).Length);
        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.CapPrefix));
        var item = Assert.Single(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => item.CorrelationKey == "pr-review-cap:" + fixture.Goal.Id.Value));
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Contains(Url, item.Body);
        Assert.Contains("rounds=3", item.Body);
        Assert.Contains("pending-reviewer", item.Body);
        Assert.Contains("Project decider: project-lead", item.Body);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal("delivered for review: " + Url, fixture.Goal.CurrentHold!.Blocker);
    }

    [Fact]
    public void Delivered_rounds_on_another_pr_do_not_consume_this_pr_cap()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        for (var round = 1; round <= 3; round++)
            fixture.Kernel.RecordGoalPolicyDecision(fixture.Goal.Id,
                $"{PullRequestReviewRound.DeliveredPrefix}https://github.com/octo/sample/pull/70 round={round} pushed={fixture.Delivery.Candidate}");
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), "fix"));

        fixture.Tick();

        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix));
        Assert.Empty(Decisions(fixture.Goal, PullRequestReviewRound.CapPrefix));
        Assert.True(PullRequestReviewRound.InFlight(fixture.Goal));
    }

    [Fact]
    public void Merged_during_round_lands_once_without_starting_pending_batch()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, ("alice", submitted, "first"));
        fixture.Tick();
        Open(fixture, ("bob", submitted.AddMinutes(1), "pending"));
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        fixture.Tick();
        fixture.Merged();
        fixture.Elapsed = TimeSpan.FromMinutes(4);

        fixture.Tick();
        fixture.Elapsed = TimeSpan.FromMinutes(6);
        Assert.Empty(fixture.Tick());

        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.All(fixture.Goal.Tasks, task => Assert.Contains(task.Status,
            new[] { WorkTaskStatus.Completed, WorkTaskStatus.Cancelled, WorkTaskStatus.Failed }));
        Assert.Equal(GoalPromotionFact.PullRequestMergedProvenance, fixture.Fact()!.Provenance);
        Assert.Single(fixture.Receipts);
        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix));
        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.StoppedPrefix));
    }

    [Fact]
    public void Direct_delivery_does_not_read_reviews_or_release_hold()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), "fix"));
        var hold = fixture.Goal.CurrentHold;
        var tracker = fixture.CreateTracker(fixture.Workspace with { Delivery = ProjectDelivery.Direct });

        Assert.Empty(tracker.Service([fixture.Goal]));

        Assert.Equal(0, fixture.Forge.Reads);
        Assert.Equal(hold, fixture.Goal.CurrentHold);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Empty(Decisions(fixture.Goal, PullRequestReviewRound.StartedPrefix));
    }

    [Fact]
    public void Reply_only_round_still_delivers_through_existing_lease()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), "explain"));
        fixture.Tick();
        var publisher = new Publisher();

        var candidate = CompleteRound(fixture, publisher, changeCode: false);

        Assert.Equal(fixture.Delivery.Candidate, candidate);
        Assert.Equal(candidate, Assert.Single(publisher.Requests).PreviouslyPushedCommit);
        Assert.Single(Decisions(fixture.Goal, PullRequestReviewRound.DeliveredPrefix));
        Assert.False(PullRequestReviewRound.InFlight(fixture.Goal));
    }

    [Fact]
    public void Ungated_round_candidate_never_publishes_or_records_delivery()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), "fix"));
        fixture.Tick();
        var candidate = PrepareCandidate(fixture, changeCode: true);
        Assert.NotEqual(fixture.Delivery.Candidate, candidate);
        var publisher = new Publisher();

        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, fixture.Workspace,
            pullRequestPublisher: publisher);

        Assert.DoesNotContain("delivered for review:", result.Message);
        Assert.Empty(publisher.Requests);
        Assert.Empty(Decisions(fixture.Goal, PullRequestReviewRound.DeliveredPrefix));
        Assert.Equal(fixture.Delivery.Candidate, fixture.Goal.DeliveredForReview!.PushedCommit);
        Assert.True(PullRequestReviewRound.InFlight(fixture.Goal));
    }

    [Fact]
    public void Rejected_lease_keeps_round_inflight_and_previous_push_provenance()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, ("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), "fix"));
        fixture.Tick();
        var candidate = PrepareCandidate(fixture, changeCode: true);
        WriteReceipt(fixture, candidate);
        var publisher = new Publisher { Reject = true };

        var result = LandingExecutor.Execute(fixture.Kernel, fixture.Goal, fixture.Workspace,
            pullRequestPublisher: publisher);

        Assert.Contains("pull request delivery failed:", result.Message);
        Assert.Equal(fixture.Delivery.Candidate, Assert.Single(publisher.Requests).PreviouslyPushedCommit);
        Assert.Equal(fixture.Delivery.Candidate, fixture.Goal.DeliveredForReview!.PushedCommit);
        Assert.Empty(Decisions(fixture.Goal, PullRequestReviewRound.DeliveredPrefix));
        Assert.True(PullRequestReviewRound.InFlight(fixture.Goal));
    }

    private static string CompleteRound(PullRequestTrackingTestFixture fixture, Publisher publisher,
        DateTimeOffset? deliveredAt = null, bool changeCode = true)
    {
        var candidate = PrepareCandidate(fixture, changeCode);
        WriteReceipt(fixture, candidate);
        var result = deliveredAt is { } at
            ? PullRequestLandingDelivery.Deliver(fixture.Kernel, fixture.Goal, fixture.Workspace,
                candidate, publisher, null, null, new FixedClock(at))
            : LandingExecutor.Execute(fixture.Kernel, fixture.Goal, fixture.Workspace, pullRequestPublisher: publisher);
        Assert.Contains("delivered for review:", result.Message);
        Assert.Equal(candidate, fixture.Goal.DeliveredForReview!.PushedCommit);
        return candidate;
    }

    private static string PrepareCandidate(PullRequestTrackingTestFixture fixture, bool changeCode)
    {
        var root = fixture.Workspace.ExecutionDirectory;
        var worktree = GoalWorktrees.WorktreePath(root, fixture.Goal.Id);
        if (changeCode)
        {
            File.AppendAllText(Path.Combine(worktree, "src", "delivery.txt"), "\nreview round fix");
            RunGit(worktree, "add", "src/delivery.txt");
            RunGit(worktree, "commit", "-m", "Address review request");
        }
        var candidate = RunGit(worktree, "rev-parse", "HEAD");
        var developer = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer);
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, developer.Id,
            ManualVerificationRecorder.Create(true, "Review request addressed.", root, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        return candidate;
    }

    private static void WriteReceipt(PullRequestTrackingTestFixture fixture, string candidate)
    {
        var round = PullRequestReviewRound.Started(fixture.Goal, Url);
        var attempt = "review-round-" + round;
        var directory = Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts", fixture.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, attempt + ".attempt.json"), JsonSerializer.Serialize(new
        {
            attemptId = attempt, goalId = fixture.Goal.Id.Value, branchHeadSha = candidate,
            kind = "gate", startedAt = DateTimeOffset.UtcNow, executionProtocol = "in-process"
        }));
        var main = fixture.Git("rev-parse", "main");
        var verification = new AcceptanceVerificationResult(true, false, 0, null,
            Checks: [new AcceptanceCheckResult("unit", true, 0, null),
                new AcceptanceCheckResult("build", true, 0, null)]) { AttemptId = attempt };
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [], [], candidate, main, null, null, null);
        var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            summary, candidate, main, null, null);
        File.WriteAllText(Path.Combine(directory, attempt + ".result.json"),
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        GoalOperationJournal.AcceptancePassed(fixture.Workspace.ExecutionDirectory, fixture.Goal, "conductor:acceptance",
            candidate, main, "passing review round gate", DateTimeOffset.UtcNow);
    }

    private static void Open(PullRequestTrackingTestFixture fixture,
        params (string Login, DateTimeOffset SubmittedAt, string Body)[] reviews) =>
        fixture.Forge.Json = JsonSerializer.Serialize(new
        {
            state = "OPEN", mergedAt = (string?)null, mergeCommit = (object?)null, closedAt = (string?)null,
            url = Url, reviewDecision = "CHANGES_REQUESTED", latestReviews = reviews.Select(review => new
            { author = new { login = review.Login }, submittedAt = review.SubmittedAt, body = review.Body, state = "CHANGES_REQUESTED" })
        });

    private static string[] Decisions(Goal goal, string prefix) => goal.Timeline.Where(item =>
        item.Kind == ProgressKind.GoalPolicyDecision && item.Message.StartsWith(prefix, StringComparison.Ordinal))
        .Select(item => item.Message).ToArray();
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string RunGit(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.Trim();
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class Publisher : IPullRequestPublisher
    {
        internal List<PullRequestPublishRequest> Requests { get; } = [];
        internal bool Reject;
        public PullRequestPublishResult Publish(PullRequestPublishRequest request)
        {
            Requests.Add(request);
            if (Reject) return new(null, null, null, PullRequestPublishFailureKind.PushRejected, "lease rejected");
            return new(7, Url, request.CandidateCommit);
        }
    }
}
