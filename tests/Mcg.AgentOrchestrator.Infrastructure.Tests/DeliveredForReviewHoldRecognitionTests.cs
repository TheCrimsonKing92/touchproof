using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Shares the Landing lane's GitRunner seam; repositories and receipts are private.
[Collection(TestCollections.LandingGitRunner)]
public sealed class DeliveredForReviewHoldRecognitionTests
{
    [Theory]
    [InlineData("touchproof[bot]")]
    [InlineData(null)]
    public void Current_delivery_with_or_without_identity_tracks_merge(string? login)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        RecordDelivery(fixture, login);
        var expected = $"delivered for review: {PullRequestLandingTestFixture.Url}" +
            (login is null ? "" : $" as {login}");
        Assert.Equal(expected, fixture.Goal.CurrentHold!.Blocker);
        Assert.Equal(expected, fixture.Goal.Timeline.Last().Message);
        Assert.True(DeliveredForReviewHold.IsCurrent(fixture.Goal));
        Assert.True(PullRequestDeliveryTracker.IsEligible(fixture.Goal));
        fixture.Merged();

        Assert.Contains(fixture.Goal.Id, fixture.Tick());

        Assert.Equal(1, fixture.Forge.Reads);
        Assert.Equal(GoalStatus.Completed, fixture.Goal.Status);
        Assert.False(DeliveredForReviewHold.IsCurrent(fixture.Goal));
        var fact = Assert.IsType<GoalPromotionFact>(fixture.Fact());
        Assert.Equal(GoalPromotionFact.PullRequestMergedProvenance, fact.Provenance);
        Assert.Equal(fixture.Delivery.Candidate, fact.CandidateRevision);
        Assert.Equal(fixture.Delivery.Candidate, fact.PromotedMainRevision);
    }

    [Fact]
    public void Suffixed_hold_changes_requested_batch_starts_review_round()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        RecordDelivery(fixture, "touchproof[bot]");
        var submitted = fixture.Goal.DeliveredForReview!.DeliveredAt.AddMinutes(1);
        fixture.Forge.Json = JsonSerializer.Serialize(new
        {
            state = "OPEN", mergedAt = (string?)null, mergeCommit = (object?)null, closedAt = (string?)null,
            url = PullRequestLandingTestFixture.Url, reviewDecision = "CHANGES_REQUESTED",
            latestReviews = new[] { new
            {
                author = new { login = "alice" }, submittedAt = submitted,
                body = "Fix the invalid result.", state = "CHANGES_REQUESTED"
            } }
        });
        Assert.True(DeliveredForReviewHold.IsCurrent(fixture.Goal));
        Assert.True(PullRequestDeliveryTracker.IsEligible(fixture.Goal));

        Assert.Contains(fixture.Goal.Id, fixture.Tick());

        Assert.Equal(1, fixture.Forge.Reads);
        var decisions = fixture.Goal.Timeline
            .Where(item => item.Kind == ProgressKind.GoalPolicyDecision).ToArray();
        var batch = Assert.Single(decisions.Where(item =>
            item.Message.StartsWith(PullRequestReviewRound.BatchPrefix, StringComparison.Ordinal)));
        var started = Assert.Single(decisions.Where(item =>
            item.Message.StartsWith(PullRequestReviewRound.StartedPrefix, StringComparison.Ordinal)));
        Assert.Equal($"pull request review round started: {PullRequestLandingTestFixture.Url} round=1 reviewers=alice",
            started.Message);
        Assert.True(Array.IndexOf(decisions, batch) < Array.IndexOf(decisions, started));
        Assert.True(PullRequestReviewRound.InFlight(fixture.Goal));
        Assert.Equal(GoalStatus.Active, fixture.Goal.Status);
        Assert.Null(fixture.Goal.CurrentHold);
        var developer = fixture.Goal.Tasks.Last(task => task.RequiredRole == AgentRole.Developer);
        var feedback = Assert.IsType<AcceptedRetryFeedback>(developer.AcceptedRetryFeedback);
        Assert.Contains("Fix the invalid result.", feedback.Message);
    }

    [Fact]
    public void Replaced_delivery_hold_does_not_track_or_land()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        RecordDelivery(fixture, "touchproof[bot]");
        ReplaceHold(fixture);
        fixture.Merged();

        Assert.False(DeliveredForReviewHold.IsCurrent(fixture.Goal));
        Assert.False(PullRequestDeliveryTracker.IsEligible(fixture.Goal));
        Assert.Empty(fixture.Tick());

        Assert.Equal(0, fixture.Forge.Reads);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Null(fixture.Fact());
    }

    [Fact]
    public void Suffixed_hold_with_trunk_drift_starts_refresh_and_invalidates_receipt()
    {
        using var fixture = new PullRequestTrackingTestFixture();
        RecordDelivery(fixture, "touchproof[bot]");
        var coordinator = WritePassedReceipt(fixture);
        LandingExecutorTests.AppendCommit(fixture.Workspace.ExecutionDirectory, "trunk.txt", "new trunk work");
        var trunk = fixture.Git("rev-parse", "main");
        Assert.NotEqual(fixture.Delivery.Main, trunk);
        fixture.Git("update-ref", "refs/remotes/origin/main", trunk);
        var tracker = new PullRequestDeliveryTracker(fixture.Kernel, fixture.Workspace,
            new GitHubPullRequestStatusReader(fixture.Forge), fixture.Forge, fixture.Attention,
            acceptanceAttempts: coordinator);

        Assert.Contains(fixture.Goal.Id, tracker.Service([fixture.Goal]));

        Assert.Equal(1, fixture.Forge.Reads);
        Assert.Single(fixture.Forge.Fetches);
        Assert.True(PullRequestLandingDelivery.HasRefreshStarted(fixture.Goal));
        Assert.Null(fixture.Goal.CurrentHold);
        Assert.False(DeliveredForReviewHold.IsCurrent(fixture.Goal));
        Assert.True(PullRequestDeliveryTracker.IsEligible(fixture.Goal));
        Assert.False(coordinator.HasPassedReconciledReceipt(fixture.Goal.Id.Value));
        Assert.NotNull(coordinator.TryReadLatest(fixture.Goal.Id.Value)!.RegateRequestedAt);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Suffixed_hold_receipt_and_console_recognition_end_on_replacement(bool replaced)
    {
        using var fixture = new PullRequestTrackingTestFixture();
        RecordDelivery(fixture, "touchproof[bot]");
        var coordinator = WritePassedReceipt(fixture);
        Assert.True(coordinator.HasPassedReconciledReceipt(fixture.Goal.Id.Value));
        if (replaced) ReplaceHold(fixture);

        Assert.Equal(replaced ? null : "delivered for review: PR 7",
            OwnerConsoleDeliveredForReviewText.For(fixture.Goal));
        var invalidated = coordinator.TryInvalidatePassedReceipt(fixture.Goal,
            PullRequestLandingTestFixture.Attempt, fixture.Delivery.Candidate,
            ConductorEvidenceSupersessionCause.CandidateChanged, "refresh delivered candidate",
            () => fixture.Delivery.Candidate);

        Assert.Equal(!replaced, invalidated);
        var receipt = Assert.IsType<ConductorParallelAcceptanceAttempt>(
            coordinator.TryReadLatest(fixture.Goal.Id.Value));
        Assert.Equal(!replaced, receipt.RegateRequestedAt is not null);
        Assert.Equal(replaced ? (ConductorEvidenceSupersessionCause?)null :
            ConductorEvidenceSupersessionCause.CandidateChanged, receipt.SupersessionCause);
    }

    [Fact]
    public void Source_has_no_delivery_hold_concatenation_outside_builder()
    {
        var root = VerifiedRepositoryRoot.Find();
        var source = Path.Combine(root, "src");
        var builder = Path.Combine(source, "Mcg.AgentOrchestrator.Core", "Domain", "DeliveredForReviewHold.cs");
        Assert.True(File.Exists(builder), "The shared delivery hold builder must exist.");
        var files = SourceFiles(source).ToArray();
        Assert.NotEmpty(files);
        Assert.Contains(builder, files);
        Assert.Empty(files.Where(path => !string.Equals(path, builder, StringComparison.OrdinalIgnoreCase) &&
            File.ReadAllText(path).Contains("\"delivered for review: \" +", StringComparison.Ordinal)));
    }

    private static void RecordDelivery(PullRequestTrackingTestFixture fixture, string? login) =>
        fixture.Kernel.RecordGoalDeliveredForReview(fixture.Goal.Id, new GoalDeliveredForReview(7,
            PullRequestLandingTestFixture.Url, fixture.Delivery.Candidate,
            PullRequestLandingTestFixture.Attempt, DateTimeOffset.UtcNow, login));

    private static void ReplaceHold(PullRequestTrackingTestFixture fixture) =>
        fixture.Kernel.ObserveGoalHold(fixture.Goal.Id, "Verified", "operator adjudication required",
            DateTimeOffset.UtcNow, TimeSpan.FromHours(1));

    private static ConductorParallelAcceptanceAttemptCoordinator WritePassedReceipt(
        PullRequestTrackingTestFixture fixture)
    {
        var root = Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts");
        var directory = Path.Combine(root, fixture.Goal.Id.Value);
        var metadata = Path.Combine(directory, PullRequestLandingTestFixture.Attempt + ".attempt.json");
        var result = Path.Combine(directory, PullRequestLandingTestFixture.Attempt + ".result.json");
        var now = DateTimeOffset.UtcNow;
        var receipt = new ConductorParallelAcceptanceAttempt(PullRequestLandingTestFixture.Attempt,
            fixture.Goal.Id.Value, fixture.Goal.Id.Value[..8], 0, fixture.Delivery.Candidate,
            fixture.Delivery.Main, now, now, 0, ConductorParallelAcceptanceAttemptOutcome.Passed,
            "", "", "", "", result, metadata, ScopePaths: ["src/delivery.txt"],
            CompletedAt: now, ReconciledAt: now, ExecutionProtocol: "in-process");
        File.WriteAllText(metadata, JsonSerializer.Serialize(receipt,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return new(root, "main", executionDirectory: fixture.Workspace.ExecutionDirectory);
    }

    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".scratch" or ".orchestrator-prototype" or
                "TestResults" or "playwright-report") continue;
            foreach (var file in SourceFiles(child)) yield return file;
        }
    }
}
