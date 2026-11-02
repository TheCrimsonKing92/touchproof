using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each test owns its repository and stores; LandingExecutor's GitRunner seam is shared.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PullRequestReviewConflictCheckTests
{
    private const string Url = PullRequestLandingTestFixture.Url;

    [Fact]
    public void Single_reviewer_starts_without_a_check()
    {
        var runner = new Runner();
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture, twoReviewers: false);
        fixture.Tick();
        Assert.Equal(0, runner.Calls);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        Assert.Empty(Decisions(fixture, PullRequestReviewConflictCheck.CheckedPrefix));
    }

    [Fact]
    public void No_conflict_records_decision_before_round_one()
    {
        var runner = new Runner { Catalog = ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.ConductorAuthor, "high") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        var latest = Open(fixture);
        fixture.Tick();
        Assert.Equal(0, PullRequestReviewRound.Started(fixture.Goal, Url));
        Next(fixture);
        Assert.Equal(1, runner.Calls);
        Assert.Equal($"{PullRequestReviewConflictCheck.CheckedPrefix}{Url} conflict=none latest={Stamp(latest)}",
            Assert.Single(Decisions(fixture, PullRequestReviewConflictCheck.CheckedPrefix)));
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var messages = fixture.Goal.Timeline.Select(item => item.Message).ToArray();
        Assert.True(Array.FindIndex(messages, message => message.StartsWith(PullRequestReviewConflictCheck.CheckedPrefix)) <
            Array.FindIndex(messages, message => message.StartsWith(PullRequestReviewRound.StartedPrefix)));
        var request = Assert.IsType<WorkerProcessRunRequest>(runner.Request);
        Assert.Contains("--permission-mode plan", request.Command);
        Assert.Contains("--model claude-sonnet-5 --effort high", request.Command);
        Assert.Contains("cannot both be satisfied by one change", request.StandardInput);
        Assert.Contains("alice", request.StandardInput);
        Assert.Contains("bob", request.StandardInput);
        var prompt = Assert.IsType<string>(request.StandardInput);
        using var reviews = JsonDocument.Parse(prompt[(prompt.IndexOf("\n\n", StringComparison.Ordinal) + 2)..]);
        Assert.Equal(latest.AddMinutes(-1), reviews.RootElement[0].GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal(latest, reviews.RootElement[1].GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Contains("Keep the cache.", request.StandardInput);
        Assert.Contains("Remove the cache.", request.StandardInput);
        Next(fixture);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
    }

    [Theory]
    [InlineData("project-lead")]
    [InlineData(null)]
    public void Conflict_waits_for_project_decider(string? decider)
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Keep versus remove the cache.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        var tracker = fixture.CreateTracker(fixture.Workspace with { DeliveryDecider = decider });
        var latest = Open(fixture);
        tracker.Service([fixture.Goal]);
        WaitForCheck(fixture);
        fixture.Elapsed += TimeSpan.FromMinutes(2);
        tracker.Service([fixture.Goal]);
        fixture.Elapsed += TimeSpan.FromMinutes(2);
        tracker.Service([fixture.Goal]);
        var item = ConflictItem(fixture);
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Contains(Url, item.Body);
        Assert.Contains("Project decider: " + (decider ?? "octo"), item.Body);
        Assert.Contains("alice,bob", item.Body);
        Assert.Contains("Keep versus remove the cache.", item.Body);
        Assert.Equal($"{PullRequestReviewConflictCheck.ConflictPrefix}{Url} latest={Stamp(latest)} reviewers=alice,bob",
            Assert.Single(Decisions(fixture, PullRequestReviewConflictCheck.ConflictPrefix)));
        AssertHeld(fixture);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("malformed", "unparseable")]
    [InlineData("exit", "exit=7")]
    [InlineData("timeout", "timed out")]
    [InlineData("cancelled", "timed out")]
    [InlineData("throw", "runner exploded")]
    [InlineData("capture", "capture limited")]
    [InlineData("shape", "unparseable")]
    [InlineData("missing-summary", "unparseable")]
    [InlineData("catalog", "catalog unreadable")]
    [InlineData("binding", "model-binding-invalid:conductor-author")]
    public void Unusable_results_raise_a_decider_item(string mode, string reason)
    {
        var runner = new Runner
        {
            Throw = mode == "throw",
            Cancel = mode == "cancelled",
            CatalogThrows = mode == "catalog",
            Catalog = mode == "binding" ? ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.ConductorAuthor, "duplicate") : null,
            Result = new(mode == "exit" ? 7 : 0,
                mode == "malformed" ? "not JSON" : mode == "shape" ? "{\"conflict\":\"false\",\"summary\":\"\"}" :
                mode == "missing-summary" ? "{\"conflict\":false}" : "{\"conflict\":false,\"summary\":\"\"}",
                "failed run", TimedOut: mode == "timeout", CaptureLimited: mode == "capture")
        };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        Assert.Contains(reason, ConflictItem(fixture).Body);
        Assert.Single(Decisions(fixture, PullRequestReviewConflictCheck.ConflictPrefix));
        AssertHeld(fixture);
        if (mode is "catalog" or "binding") Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Missing_runner_fails_closed_to_decider()
    {
        using var fixture = new PullRequestTrackingTestFixture(new PullRequestReviewConflictCheck());
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        Assert.Contains("runner not configured", ConflictItem(fixture).Body);
        AssertHeld(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Running_check_does_not_block_pass_or_terminal_tracking(bool merged)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new PullRequestReviewConflictCheck((_, token) =>
        {
            entered.SetResult();
            using var registration = token.Register(() => { cancelled.SetResult(); release.Set(); });
            release.Wait(); // Deliberately synchronous: regression would block the calling service pass.
            return Task.FromResult(new WorkerProcessRunResult(0, "{\"conflict\":false,\"summary\":\"\"}", ""));
        });
        using var fixture = new PullRequestTrackingTestFixture(check);
        Open(fixture);
        var watchdog = new Timer(_ => release.Set(), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        try
        {
            fixture.Tracker.Service([fixture.Goal]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: runner entered.
            Assert.True(check.PendingTask(fixture.Goal.Id) is { IsCompleted: false });
            fixture.Elapsed += TimeSpan.FromMinutes(2);
            fixture.Tracker.Service([fixture.Goal]);
            AssertHeld(fixture);
            Assert.Empty(fixture.Forge.Fetches); // Refresh cannot move the pending batch boundary.
            if (merged) fixture.Merged();
            else fixture.Forge.Json = """{"state":"CLOSED","mergedAt":null,"mergeCommit":null,"closedAt":"2026-10-31T12:00:00Z","url":"https://github.com/octo/sample/pull/7","reviewDecision":null,"latestReviews":[]}""";
            fixture.Elapsed += TimeSpan.FromMinutes(2);
            fixture.Tracker.Service([fixture.Goal]);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: abandonment cancelled runner.
            if (merged)
            {
                Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
                Assert.Single(fixture.Receipts);
            }
            else
            {
                Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
                Assert.Single(Decisions(fixture, PullRequestLandingDelivery.ClosedUnmergedPrefix));
            }
            Assert.Null(check.PendingTask(fixture.Goal.Id));
        }
        finally { release.Set(); await watchdog.DisposeAsync(); }
    }

    [Fact]
    public void Answer_survives_restart_and_precedes_instruction()
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        AssertHeld(fixture);
        const string ruling = "Keep the cache and add an explicit invalidation API.";
        fixture.Attention.TryResolveAsync(PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value, ruling)
            .GetAwaiter().GetResult();
        // New tracker and new check have no process-local knowledge of this batch.
        var restarted = new PullRequestDeliveryTracker(fixture.Kernel, fixture.Workspace,
            new GitHubPullRequestStatusReader(fixture.Forge), fixture.Forge, fixture.Attention,
            conflictCheck: new PullRequestReviewConflictCheck());
        restarted.Service([fixture.Goal]);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var guidance = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer).AcceptedRetryFeedback!.Message;
        Assert.Contains("Decider ruling: " + ruling, guidance);
        Assert.True(guidance.IndexOf("Decider ruling: " + ruling, StringComparison.Ordinal) <
            guidance.IndexOf(PullRequestReviewRound.Instruction, StringComparison.Ordinal));
        Assert.Empty(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => item.CorrelationKey == PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value));
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Park_resolution_after_unpark_requires_explicit_ruling(bool legacy)
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        var item = ConflictItem(fixture);
        const string reason = "Wait for the project decider";
        fixture.Kernel.ParkGoal(fixture.Goal.Id, reason);
        Assert.Equal(GoalStatus.Parked, fixture.Goal.Status);
        var resolution = legacy ? "Goal parked." : GoalParkResolution.Build(reason);
        // Same store operation as the CLI park transition, including legacy answer hydration.
        Assert.True(fixture.Attention.ResolveOpenForGoalAsync(fixture.Goal.Id.Value, resolution)
            .GetAwaiter().GetResult() > 0);
        var resolved = fixture.Attention.ListAsync(fixture.Goal.Id.Value).GetAwaiter().GetResult()
            .Single(row => row.Id == item.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
        Assert.Equal(resolution, resolved.AuthoritativeAnswer!.Text);
        fixture.Kernel.UnparkGoal(fixture.Goal.Id, "Resume review tracking");
        AssertHeld(fixture);

        Next(fixture);

        AssertHeld(fixture);
        var raised = ConflictItem(fixture);
        Assert.NotEqual(item.Id, raised.Id);
        Assert.Equal(item.Body, raised.Body);
        Next(fixture);
        AssertHeld(fixture);
        Assert.Equal(raised.Id, ConflictItem(fixture).Id);
        Assert.Equal(1, runner.Calls);

        const string ruling = "Keep the cache and add an invalidation API.";
        Assert.True(fixture.Attention.TryResolveAsync(raised.CorrelationKey!, ruling).GetAwaiter().GetResult());
        Next(fixture);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var guidance = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer).AcceptedRetryFeedback!.Message;
        Assert.Contains("Decider ruling: " + ruling, guidance);
        Assert.DoesNotContain(resolution, guidance);
        Assert.True(guidance.IndexOf("Decider ruling: " + ruling, StringComparison.Ordinal) <
            guidance.IndexOf(PullRequestReviewRound.Instruction, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult(),
            row => row.CorrelationKey == item.CorrelationKey);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Later_batch_gets_new_check_and_cannot_reuse_old_answer()
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        var latest = Open(fixture);
        fixture.Tick();
        Next(fixture);
        fixture.Attention.TryResolveAsync(PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value, "Old ruling")
            .GetAwaiter().GetResult();
        Next(fixture);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var developer = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer);
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, developer.Id,
            ManualVerificationRecorder.Create(true, "Addressed ruling", fixture.Workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
        fixture.Kernel.RecordGoalDeliveredForReview(fixture.Goal.Id, fixture.Goal.DeliveredForReview!);
        RecordDelivered(fixture, 1);
        Open(fixture, offset: 10);
        Next(fixture);
        Next(fixture);
        Assert.Equal(2, runner.Calls);
        Assert.Contains(Stamp(latest.AddMinutes(10)), ConflictItem(fixture).Body);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal("delivered for review: " + Url, fixture.Goal.CurrentHold!.Blocker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Changed_batch_cannot_bypass_unanswered_conflict(bool twoReviewers)
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        var latest = Open(fixture);
        fixture.Tick();
        Next(fixture);
        var item = ConflictItem(fixture);
        runner.Result = new(0, "{\"conflict\":false,\"summary\":\"Compatible new requests.\"}", "");
        Open(fixture, twoReviewers, offset: 10);
        Next(fixture);
        Next(fixture);
        AssertHeld(fixture);
        Assert.Equal(item.Id, ConflictItem(fixture).Id);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(Decisions(fixture, PullRequestReviewConflictCheck.CheckedPrefix));
        Assert.Contains(Stamp(latest), item.Body);
        Assert.True(fixture.Attention.TryResolveAsync(item.CorrelationKey!, "Keep the cache")
            .GetAwaiter().GetResult());
        Next(fixture);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var guidance = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer).AcceptedRetryFeedback!.Message;
        Assert.Contains("Decider ruling: Keep the cache", guidance);
        Assert.Contains(Stamp(latest.AddMinutes(twoReviewers ? 10 : 9)), guidance);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void Conflict_closed_without_answer_is_raised_again()
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        var item = ConflictItem(fixture);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Workspace.OrchestratorDirectory, "collaboration-items.db"), Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE collaboration_items SET status = 'Closed' WHERE id = $id";
            command.Parameters.AddWithValue("$id", item.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var closed = fixture.Attention.ListAsync(fixture.Goal.Id.Value).GetAwaiter().GetResult().Single(row => row.Id == item.Id);
        Assert.Equal(CollaborationItemStatus.Closed, closed.Status);
        Assert.Null(closed.AuthoritativeAnswer);
        Next(fixture);
        var raised = ConflictItem(fixture);
        Assert.NotEqual(item.Id, raised.Id);
        Assert.Equal(item.Body, raised.Body);
        AssertHeld(fixture);
        Next(fixture);
        Assert.Equal(raised.Id, ConflictItem(fixture).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Withdrawn_requests_consume_pending_check_before_refresh(bool conflict)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource<WorkerProcessRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new PullRequestReviewConflictCheck((_, token) =>
        {
            entered.SetResult();
            return output.Task.WaitAsync(token);
        });
        using var fixture = new PullRequestTrackingTestFixture(check);
        Open(fixture);
        fixture.Tracker.Service([fixture.Goal]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: runner entered.
        var pending = Assert.IsAssignableFrom<Task>(check.PendingTask(fixture.Goal.Id));
        Assert.False(pending.IsCompleted);
        Assert.Empty(fixture.Forge.Fetches);
        Approve(fixture);
        fixture.Elapsed += TimeSpan.FromMinutes(2);
        fixture.Tracker.Service([fixture.Goal]);
        Assert.Same(pending, check.PendingTask(fixture.Goal.Id));
        Assert.Empty(fixture.Forge.Fetches);
        AssertHeld(fixture);
        output.SetResult(new WorkerProcessRunResult(0,
            conflict ? "not JSON" : "{\"conflict\":false,\"summary\":\"\"}", ""));
        await pending.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: fake runner result received.
        Next(fixture);
        Assert.Null(check.PendingTask(fixture.Goal.Id));
        if (conflict)
        {
            Assert.Contains("unparseable", ConflictItem(fixture).Body);
            Assert.True(check.Holds(fixture.Goal, Url));
            Assert.Empty(fixture.Forge.Fetches);
            Assert.True(fixture.Attention.TryResolveAsync(PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value,
                "No change needed").GetAwaiter().GetResult());
            Next(fixture);
        }
        else Assert.Single(Decisions(fixture, PullRequestReviewConflictCheck.CheckedPrefix));
        Assert.False(check.Holds(fixture.Goal, Url));
        Assert.Single(fixture.Forge.Fetches);
        AssertHeld(fixture);
    }

    [Fact]
    public void Withdrawn_conflict_releases_refresh_only_after_ruling()
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        Approve(fixture);
        Next(fixture);
        Assert.True(fixture.ConflictCheck.Holds(fixture.Goal, Url));
        Assert.Empty(fixture.Forge.Fetches);
        var item = ConflictItem(fixture);
        Assert.True(fixture.Attention.TryResolveAsync(item.CorrelationKey!, "No change needed")
            .GetAwaiter().GetResult());
        Next(fixture);
        Assert.False(fixture.ConflictCheck.Holds(fixture.Goal, Url));
        Assert.Single(fixture.Forge.Fetches);
        AssertHeld(fixture);
        // The consumed ruling cannot authorize a later batch even without a prior round start.
        Open(fixture, offset: 10);
        Next(fixture);
        Next(fixture);
        Assert.Equal(2, runner.Calls);
        Assert.NotEqual(item.Id, ConflictItem(fixture).Id);
        AssertHeld(fixture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Terminal_status_resolves_open_conflict(bool merged)
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        var item = ConflictItem(fixture);
        if (merged) fixture.Merged();
        else fixture.Forge.Json = """{"state":"CLOSED","mergedAt":null,"mergeCommit":null,"closedAt":"2026-10-31T12:00:00Z","url":"https://github.com/octo/sample/pull/7","reviewDecision":null,"latestReviews":[]}""";
        Next(fixture);
        Assert.DoesNotContain(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult(),
            row => row.CorrelationKey == item.CorrelationKey);
        Assert.Equal(CollaborationItemStatus.Resolved, fixture.Attention.ListAsync(fixture.Goal.Id.Value)
            .GetAwaiter().GetResult().Single(row => row.Id == item.Id).Status);
        Assert.Equal(0, PullRequestReviewRound.Started(fixture.Goal, Url));
        Assert.Equal(merged ? GoalStatus.Completed : GoalStatus.Verified, fixture.Goal.Status);
        Assert.False(fixture.ConflictCheck.Holds(fixture.Goal, Url));
    }

    [Fact]
    public void Ruled_round_counts_toward_cap()
    {
        var runner = new Runner { Result = new(0, "{\"conflict\":true,\"summary\":\"Cache conflict.\"}", "") };
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        fixture.Tick();
        Next(fixture);
        fixture.Attention.TryResolveAsync(PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value, "Keep it")
            .GetAwaiter().GetResult();
        // Two prior delivered rounds plus this ruled round exhaust the unchanged cap.
        for (var round = 1; round <= 2; round++) RecordDelivered(fixture, round);
        Next(fixture);
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        var developer = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer);
        fixture.Kernel.RecordTaskVerification(fixture.Goal.Id, developer.Id,
            ManualVerificationRecorder.Create(true, "Addressed ruling", fixture.Workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
        fixture.Kernel.RecordGoalDeliveredForReview(fixture.Goal.Id, fixture.Goal.DeliveredForReview!);
        RecordDelivered(fixture, 3);
        Open(fixture, offset: 10);
        Next(fixture);
        Assert.Equal(1, runner.Calls);
        Assert.Single(Decisions(fixture, PullRequestReviewRound.CapPrefix));
        Assert.Equal(1, PullRequestReviewRound.Started(fixture.Goal, Url));
        Assert.Contains(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult(),
            item => item.CorrelationKey == "pr-review-cap:" + fixture.Goal.Id.Value);
    }

    [Fact]
    public void Direct_delivery_runs_no_check()
    {
        var runner = new Runner();
        using var fixture = new PullRequestTrackingTestFixture(runner.Check);
        Open(fixture);
        var tracker = fixture.CreateTracker(fixture.Workspace with { Delivery = ProjectDelivery.Direct });
        Assert.Empty(tracker.Service([fixture.Goal]));
        Assert.Equal(0, runner.Calls);
        Assert.Equal(0, fixture.Forge.Reads);
    }

    private static void RecordDelivered(PullRequestTrackingTestFixture fixture, int round) =>
        fixture.Kernel.RecordGoalPolicyDecision(fixture.Goal.Id,
            $"{PullRequestReviewRound.DeliveredPrefix}{Url} round={round} pushed={fixture.Delivery.Candidate}");
    private static DateTimeOffset Open(PullRequestTrackingTestFixture fixture, bool twoReviewers = true, int offset = 0)
    {
        var first = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1 + offset);
        var reviews = new[] { new { author = new { login = "alice" }, submittedAt = first, body = "Keep the cache.", state = "CHANGES_REQUESTED" },
            new { author = new { login = "bob" }, submittedAt = first.AddMinutes(1), body = "Remove the cache.", state = "CHANGES_REQUESTED" } };
        fixture.Forge.Json = JsonSerializer.Serialize(new
        {
            state = "OPEN", mergedAt = (string?)null, mergeCommit = (object?)null, closedAt = (string?)null,
            url = Url, reviewDecision = "CHANGES_REQUESTED", latestReviews = reviews.Take(twoReviewers ? 2 : 1)
        });
        return first.AddMinutes(twoReviewers ? 1 : 0);
    }
    private static void Next(PullRequestTrackingTestFixture fixture)
    { fixture.Elapsed += TimeSpan.FromMinutes(2); fixture.Tick(); }
    private static void Approve(PullRequestTrackingTestFixture fixture) => fixture.Forge.Json =
        """{"state":"OPEN","mergedAt":null,"mergeCommit":null,"closedAt":null,"url":"https://github.com/octo/sample/pull/7","reviewDecision":"APPROVED","latestReviews":[]}""";
    private static void WaitForCheck(PullRequestTrackingTestFixture fixture) =>
        fixture.ConflictCheck.PendingTask(fixture.Goal.Id)!.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    private static CollaborationItem ConflictItem(PullRequestTrackingTestFixture fixture) =>
        Assert.Single(fixture.Attention.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => item.CorrelationKey == PullRequestReviewConflictCheck.KeyPrefix + fixture.Goal.Id.Value));
    private static void AssertHeld(PullRequestTrackingTestFixture fixture)
    {
        Assert.Equal(0, PullRequestReviewRound.Started(fixture.Goal, Url));
        Assert.DoesNotContain(fixture.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal("delivered for review: " + Url, fixture.Goal.CurrentHold!.Blocker);
    }
    private static string[] Decisions(PullRequestTrackingTestFixture fixture, string prefix) => fixture.Goal.Timeline
        .Where(item => item.Kind == ProgressKind.GoalPolicyDecision && item.Message.StartsWith(prefix, StringComparison.Ordinal))
        .Select(item => item.Message).ToArray();
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed class Runner
    {
        internal int Calls;
        internal WorkerProcessRunRequest? Request;
        internal WorkerProcessRunResult Result = new(0, "{\"conflict\":false,\"summary\":\"\"}", "");
        internal bool Throw;
        internal bool Cancel;
        internal bool CatalogThrows;
        internal ModelFunctionCatalog? Catalog;
        internal PullRequestReviewConflictCheck Check => new((request, _) =>
        {
            Interlocked.Increment(ref Calls);
            Request = request;
            if (Throw) throw new InvalidOperationException("runner exploded");
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(Result);
        }, () => CatalogThrows ? throw new IOException("catalog unreadable") : Catalog);
    }
}
