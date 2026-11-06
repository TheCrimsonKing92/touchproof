using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: repositories and attention stores are private; GitRunner overrides are flow-local.
public sealed class PullRequestChangesRequestedTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    private const string Prefix = "pull request changes requested: ";

    [Theory]
    [InlineData(null, "octo")]
    [InlineData("lead", "lead")]
    public void Qualifying_batch_records_decision_and_names_reviewer_excerpt_and_decider(string? configured, string expected)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var project = new OrchestratorProject("sample", fixture.Workspace.ExecutionDirectory)
        { Delivery = ProjectDelivery.PullRequest, DeliveryDecider = configured };
        var tracker = fixture.CreateTracker(fixture.Workspace with { DeliveryDecider = project.ResolveWorkspace().DeliveryDecider });
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, "CHANGES_REQUESTED", Review("alice", submitted, "Please fix \"this\"."));
        var tasks = fixture.Goal.Tasks.Count;

        Assert.Contains(fixture.Goal.Id, tracker.Service([fixture.Goal]));

        Assert.Equal($"{Prefix}{PullRequestLandingTestFixture.Url} reviewers=alice latest={Stamp(submitted)}",
            Assert.Single(Decisions(fixture.Goal)).Message);
        var item = Assert.Single(OpenItems(fixture));
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal("pr-changes-requested:" + fixture.Goal.Id.Value, item.CorrelationKey);
        Assert.Contains("alice: \"Please fix \"\"this\"\".\"", item.Body);
        Assert.Contains("Project decider: " + expected, item.Body);
        Assert.Contains(PullRequestLandingTestFixture.Url, item.Body);
        Assert.Contains("A Developer review round addresses this batch.", item.Body);
        Assert.Equal(tasks, fixture.Goal.Tasks.Count);
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
    }

    [Fact]
    public void Same_batch_is_suppressed_after_tracking_pass_and_persisted_reload()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, "CHANGES_REQUESTED", Review("alice", submitted));
        fixture.Tick();
        var item = Assert.Single(OpenItems(fixture));
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        Assert.Empty(fixture.Tick());
        Assert.Single(Decisions(fixture.Goal));
        fixture.Repository.SaveAsync(fixture.Kernel).GetAwaiter().GetResult();
        var restored = fixture.Repository.LoadAsync().GetAwaiter().GetResult();
        var goal = restored.GetGoal(fixture.Goal.Id);
        var attention = CollaborationItemStore.ForDirectory(fixture.Workspace.OrchestratorDirectory);
        var tracker = new PullRequestDeliveryTracker(restored, fixture.Workspace,
            new GitHubPullRequestStatusReader(fixture.Forge), fixture.Forge, attention);
        // Different body and timezone representation still have the same batch identity.
        Open(fixture, "CHANGES_REQUESTED", Review("alice", submitted.ToOffset(TimeSpan.FromHours(2)), "edited"));

        Assert.Empty(tracker.Service([goal]));

        Assert.Single(Decisions(goal));
        Assert.Equal(item.Id, Assert.Single(OpenItems(fixture)).Id);
        Assert.Equal(item.Body, Assert.Single(OpenItems(fixture)).Body);
        Assert.Equal(3, fixture.Forge.Reads);
    }

    [Fact]
    public void Newer_batch_records_again_and_updates_the_single_open_item()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, "CHANGES_REQUESTED", Review("zoe", submitted));
        fixture.Tick();
        var original = Assert.Single(OpenItems(fixture));
        Open(fixture, "CHANGES_REQUESTED", Review("zoe", submitted), Review("alice", submitted.AddMinutes(1), "new issue"));
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        fixture.Tick();

        Assert.Equal(2, Decisions(fixture.Goal).Length);
        Assert.Equal($"{Prefix}{PullRequestLandingTestFixture.Url} reviewers=alice,zoe latest={Stamp(submitted.AddMinutes(1))}",
            Decisions(fixture.Goal)[1].Message);
        var item = Assert.Single(OpenItems(fixture));
        Assert.Equal(original.Id, item.Id);
        Assert.Contains("alice: \"new issue\"", item.Body);
        Assert.Contains("zoe:", item.Body);
    }

    [Theory]
    [InlineData("CHANGES_REQUESTED", "COMMENTED")]
    [InlineData("CHANGES_REQUESTED", "APPROVED")]
    [InlineData("REVIEW_REQUIRED", "CHANGES_REQUESTED")]
    [InlineData("APPROVED", "CHANGES_REQUESTED")]
    [InlineData(null, "CHANGES_REQUESTED")]
    [InlineData("", "CHANGES_REQUESTED")]
    public void Non_qualifying_reviews_record_nothing(string? decision, string state)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, decision, Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), state: state));

        Assert.Empty(fixture.Tick());

        Assert.Empty(Decisions(fixture.Goal));
        Assert.Empty(OpenItems(fixture));
        Assert.Equal(1, fixture.Forge.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Review_at_or_before_delivery_is_ignored(int minutes)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, "CHANGES_REQUESTED", Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(minutes)));

        Assert.Empty(fixture.Tick());

        Assert.Empty(Decisions(fixture.Goal));
        Assert.Empty(OpenItems(fixture));
    }

    [Fact]
    public void Refresh_boundary_excludes_reviews_at_or_before_it()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var refreshedAt = fixture.Goal.DeliveredForReview!.DeliveredAt.AddHours(1);
        var kernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), new FixedClock(refreshedAt));
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var tracker = new PullRequestDeliveryTracker(kernel, fixture.Workspace,
            new GitHubPullRequestStatusReader(fixture.Forge), fixture.Forge, fixture.Attention, elapsed: () => fixture.Elapsed);
        kernel.RecordGoalPolicyDecision(goal.Id,
            PullRequestLandingDelivery.RefreshStartedPrefix + PullRequestLandingTestFixture.Url);
        Open(fixture, "CHANGES_REQUESTED", Review("before", refreshedAt.AddTicks(-1)), Review("equal", refreshedAt));

        Assert.Empty(tracker.Service([goal]));
        Assert.Empty(Decisions(goal));
        Assert.Empty(OpenItems(fixture));
        Open(fixture, "CHANGES_REQUESTED", Review("after", refreshedAt.AddTicks(1)));
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        Assert.Contains(goal.Id, tracker.Service([goal]));
        Assert.Contains("reviewers=after", Assert.Single(Decisions(goal)).Message);
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("REVIEW_REQUIRED")]
    [InlineData(null)]
    public void Changed_review_decision_resolves_item_and_new_batch_reopens_attention(string? decision)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        Open(fixture, "CHANGES_REQUESTED", Review("alice", submitted));
        fixture.Tick();
        var item = Assert.Single(OpenItems(fixture));
        Open(fixture, decision);
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        fixture.Tick();

        Assert.Empty(OpenItems(fixture));
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(AllItems(fixture)).Status);
        Assert.Single(Decisions(fixture.Goal));
        Open(fixture, "CHANGES_REQUESTED", Review("bob", submitted.AddMinutes(1)));
        fixture.Elapsed = TimeSpan.FromMinutes(4);
        fixture.Tick();
        var reopened = Assert.Single(OpenItems(fixture));
        Assert.Equal(item.CorrelationKey, reopened.CorrelationKey);
        Assert.Contains("bob:", reopened.Body);
        Assert.Equal(2, Decisions(fixture.Goal).Length);
    }

    [Theory]
    [InlineData("shape")]
    [InlineData("missing")]
    [InlineData("author")]
    [InlineData("timestamp")]
    [InlineData("body")]
    [InlineData("state")]
    [InlineData("decision")]
    [InlineData("missing-decision")]
    [InlineData("ignored-review")]
    public void Malformed_review_fields_are_unavailable_and_preserve_existing_attention(string fault)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, "CHANGES_REQUESTED", Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1)));
        fixture.Tick();
        var original = Assert.Single(OpenItems(fixture));
        var json = JsonNode.Parse(fixture.Forge.Json)!.AsObject();
        var review = json["latestReviews"]![0]!.AsObject();
        switch (fault)
        {
            case "shape": json["latestReviews"] = "invalid"; break;
            case "missing": json.Remove("latestReviews"); break;
            case "author": review.Remove("author"); break;
            case "timestamp": review["submittedAt"] = "invalid"; break;
            case "body": review["body"] = 3; break;
            case "state": review["state"] = null; break;
            case "decision": json["reviewDecision"] = 3; break;
            case "missing-decision": json.Remove("reviewDecision"); break;
            case "ignored-review": review["state"] = "COMMENTED"; review["submittedAt"] = null; break;
        }
        fixture.Forge.Json = json.ToJsonString();

        Assert.Equal(PullRequestStatusKind.Unavailable,
            new GitHubPullRequestStatusReader(fixture.Forge).Read(fixture.Workspace.ExecutionDirectory, 7).Kind);
        fixture.Elapsed = TimeSpan.FromMinutes(2);
        Assert.Empty(fixture.Tick());

        Assert.Equal(original, Assert.Single(OpenItems(fixture)));
        Assert.Single(Decisions(fixture.Goal));
    }

    [Fact]
    public void Empty_qualifying_batch_leaves_existing_attention_unchanged()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, "CHANGES_REQUESTED", Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1)));
        fixture.Tick();
        var item = Assert.Single(OpenItems(fixture));
        Open(fixture, "CHANGES_REQUESTED");
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        Assert.Empty(fixture.Tick());
        Assert.Equal(item, Assert.Single(OpenItems(fixture)));
        Assert.Single(Decisions(fixture.Goal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Terminal_status_resolves_attention_and_merged_path_still_lands(bool merged)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        Open(fixture, "CHANGES_REQUESTED", Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1)));
        fixture.Tick();
        Assert.Single(OpenItems(fixture));
        fixture.Merged();
        if (!merged)
        {
            var json = JsonNode.Parse(fixture.Forge.Json)!;
            json["state"] = "CLOSED";
            json["mergedAt"] = null;
            json["mergeCommit"] = null;
            fixture.Forge.Json = json.ToJsonString();
        }
        fixture.Elapsed = TimeSpan.FromMinutes(2);

        fixture.Tick();

        Assert.Empty(OpenItems(fixture));
        Assert.Equal(CollaborationItemStatus.Resolved, Assert.Single(AllItems(fixture)).Status);
        if (merged)
        {
            Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
            Assert.Equal(GoalPromotionFact.PullRequestMergedProvenance, fixture.Fact()!.Provenance);
            Assert.Equal(fixture.Delivery.Candidate, fixture.Fact()!.PromotedMainRevision);
            Assert.Single(fixture.Receipts);
        }
        else
        {
            Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
            Assert.True(PullRequestReviewRound.Stopped(fixture.Goal));
            Assert.Null(fixture.Fact());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("long")]
    public void Review_mapping_preserves_empty_body_and_truncates_at_400_unicode_characters(string kind)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        var expected = kind == "long" ? string.Concat(Enumerable.Repeat("😀", 400)) : kind;
        var body = kind == "long" ? expected + "excluded" : expected;
        Open(fixture, "CHANGES_REQUESTED", Review("alice", fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1), body));

        var status = new GitHubPullRequestStatusReader(fixture.Forge).Read(fixture.Workspace.ExecutionDirectory, 7);

        Assert.Equal(PullRequestStatusKind.Open, status.Kind);
        Assert.Equal(expected, Assert.Single(status.ChangesRequested!).Body);
        fixture.Tick();
        Assert.Contains("alice: \"" + expected + "\"", Assert.Single(OpenItems(fixture)).Body);
        Assert.DoesNotContain("excluded", Assert.Single(OpenItems(fixture)).Body);
    }

    private static object Review(string login, DateTimeOffset submittedAt, string body = "Please fix this.",
        string state = "CHANGES_REQUESTED") => new { author = new { login }, submittedAt, body, state };

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static void Open(PullRequestTrackingTestFixture fixture, string? decision, params object[] reviews) =>
        fixture.Forge.Json = JsonSerializer.Serialize(new
        {
            state = "OPEN", mergedAt = (string?)null, mergeCommit = (object?)null, closedAt = (string?)null,
            url = PullRequestLandingTestFixture.Url, reviewDecision = decision, latestReviews = reviews
        });

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static ProgressEvent[] Decisions(Goal goal) => goal.Timeline.Where(item =>
        item.Kind == ProgressKind.GoalPolicyDecision && item.Message.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
    private static CollaborationItem[] AllItems(PullRequestTrackingTestFixture fixture) =>
        fixture.Attention.ListAsync(fixture.Goal.Id.Value).GetAwaiter().GetResult()
            .Where(item => item.CorrelationKey == "pr-changes-requested:" + fixture.Goal.Id.Value).ToArray();
    private static CollaborationItem[] OpenItems(PullRequestTrackingTestFixture fixture) => AllItems(fixture)
        .Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status)).ToArray();
}
