using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: private git fixtures and in-memory kernels; landing runner hooks are flow-local.
public sealed class PostLandingFollowUpAttentionTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    private static readonly DateTimeOffset RenderedAt =
        new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public void AllViews_OpenCheck_ReportsRequestTimeAgeAndSummary()
    {
        var clock = new RequestClock(RenderedAt.AddSeconds(-90).ToOffset(TimeSpan.FromHours(5.5)));
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Age of a landed follow-up");
        var wait = PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability);
        kernel = WithCompletedGoals(kernel);

        foreach (var rendered in RenderViews(kernel, new RenderClock(RenderedAt)))
        {
            AssertFollowUpRow(rendered, goal, wait);
            using var document = JsonDocument.Parse(rendered);
            Assert.Equal("2026-11-14T12:00:00.1234567Z",
                document.RootElement.GetProperty("generatedAt").GetString());
            var payload = document.RootElement.GetProperty("payload");
            var row = Assert.Single(payload.GetProperty("postLandingFollowUps").EnumerateArray());
            Assert.Equal("2026-11-14T11:58:30.1234567Z", row.GetProperty("requestedAt").GetString());
            Assert.Equal("90", row.GetProperty("ageSeconds").GetRawText());
            AssertSummary(payload, 1, 90, wait.Id.Value);
        }
    }

    [Fact]
    public void AllViews_AdvancingClock_UsesOneInstantForGeneratedAtAndAges()
    {
        var kernel = new AgentOrchestratorKernel(new RequestClock(RenderedAt.AddSeconds(-90)));
        var goal = kernel.CreateGoal("One render instant");
        PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability);
        kernel = WithCompletedGoals(kernel);
        var clock = new RenderClock(RenderedAt, TimeSpan.FromSeconds(1));

        foreach (var rendered in RenderViews(kernel, clock))
        {
            using var document = JsonDocument.Parse(rendered);
            var root = document.RootElement;
            var row = Assert.Single(root.GetProperty("payload").GetProperty("postLandingFollowUps").EnumerateArray());
            var elapsed = root.GetProperty("generatedAt").GetDateTimeOffset() -
                row.GetProperty("requestedAt").GetDateTimeOffset();
            Assert.Equal((long)elapsed.TotalSeconds, row.GetProperty("ageSeconds").GetInt64());
        }
        Assert.Equal(3, clock.Reads);
    }

    [Fact]
    public void Summary_ThreeChecks_CountsEmittedRowsAndSelectsOldest()
    {
        var clock = new RequestClock(RenderedAt.AddSeconds(-10));
        var kernel = new AgentOrchestratorKernel(clock);
        var firstGoal = kernel.CreateGoal("First landed goal");
        var secondGoal = kernel.CreateGoal("Second landed goal");
        var newest = PostLandingOperatorCheckTests.Request(kernel, firstGoal,
            PostLandingOperatorCheck.Availability, "Ten-second observation");
        clock.UtcNow = RenderedAt.AddSeconds(-300);
        var oldest = PostLandingOperatorCheckTests.Request(kernel, secondGoal,
            PostLandingOperatorCheck.Availability, "Five-minute observation");
        clock.UtcNow = RenderedAt.AddSeconds(-45);
        var middle = PostLandingOperatorCheckTests.Request(kernel, firstGoal,
            PostLandingOperatorCheck.Availability, "Forty-five-second observation");
        kernel = WithCompletedGoals(kernel);

        foreach (var rendered in RenderViews(kernel, new RenderClock(RenderedAt)))
        {
            using var document = JsonDocument.Parse(rendered);
            var payload = document.RootElement.GetProperty("payload");
            Assert.Equal(new[] { newest.Id.Value, oldest.Id.Value, middle.Id.Value },
                payload.GetProperty("postLandingFollowUps").EnumerateArray()
                    .Select(row => row.GetProperty("waitId").GetString()).ToArray());
            AssertSummary(payload, 3, 300, oldest.Id.Value);
        }

        using var singleGoal = JsonDocument.Parse(AttentionShowAgentReadView.Render(firstGoal.Id.Value,
            kernel.HumanInputRequests.ToArray(), [], _ => null, new RenderClock(RenderedAt), kernel.Goals));
        var singlePayload = singleGoal.RootElement.GetProperty("payload");
        Assert.Equal(2, singlePayload.GetProperty("postLandingFollowUps").GetArrayLength());
        AssertSummary(singlePayload, 2, 45, middle.Id.Value);
    }

    [Fact]
    public void AllViews_FutureRequest_ClampsAgeToZero()
    {
        var kernel = new AgentOrchestratorKernel(new RequestClock(RenderedAt.AddMinutes(1)));
        var goal = kernel.CreateGoal("Future-dated check");
        var wait = PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability);
        kernel = WithCompletedGoals(kernel);

        foreach (var rendered in RenderViews(kernel, new RenderClock(RenderedAt)))
        {
            using var document = JsonDocument.Parse(rendered);
            var payload = document.RootElement.GetProperty("payload");
            var row = Assert.Single(payload.GetProperty("postLandingFollowUps").EnumerateArray());
            Assert.Equal("0", row.GetProperty("ageSeconds").GetRawText());
            AssertSummary(payload, 1, 0, wait.Id.Value);
        }
    }

    [Fact]
    public void Summary_EqualTimestamps_SelectsOrdinallySmallestWaitId()
    {
        var kernel = new AgentOrchestratorKernel(new RequestClock(RenderedAt.AddSeconds(-90.9)));
        var goal = kernel.CreateGoal("Tied request timestamps");
        var first = PostLandingOperatorCheckTests.Request(kernel, goal,
            PostLandingOperatorCheck.Availability, "First observation");
        var second = PostLandingOperatorCheckTests.Request(kernel, goal,
            PostLandingOperatorCheck.Availability, "Second observation");
        kernel = WithCompletedGoals(kernel);
        var ordered = new[] { first, second }.OrderBy(wait => wait.Id.Value, StringComparer.Ordinal).ToArray();

        // Feed the larger id first so input order cannot accidentally satisfy the tie-break.
        using var document = JsonDocument.Parse(AttentionAgentReadView.RenderSummary(
            ordered.Reverse().ToArray(), [], new RenderClock(RenderedAt), goals: kernel.Goals));
        var payload = document.RootElement.GetProperty("payload");
        Assert.All(payload.GetProperty("postLandingFollowUps").EnumerateArray(),
            row => Assert.Equal("90", row.GetProperty("ageSeconds").GetRawText()));
        AssertSummary(payload, 2, 90, ordered[0].Id.Value);
    }

    [Fact]
    public void Summary_SameWholeSecondAge_SelectsEarlierTimestamp()
    {
        var clock = new RequestClock(RenderedAt.AddSeconds(-90));
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Subsecond oldest request");
        var first = PostLandingOperatorCheckTests.Request(kernel, goal,
            PostLandingOperatorCheck.Availability, "First observation");
        var second = PostLandingOperatorCheckTests.Request(kernel, goal,
            PostLandingOperatorCheck.Availability, "Second observation");
        var ordered = new[] { first, second }.OrderBy(wait => wait.Id.Value, StringComparer.Ordinal).ToArray();
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            HumanInputRequests = snapshot.HumanInputRequests.Select(wait => wait with
            {
                RequestedAt = wait.Id == ordered[1].Id.Value ? RenderedAt.AddSeconds(-90.9) : RenderedAt.AddSeconds(-90.1)
            }).ToArray()
        });
        kernel = WithCompletedGoals(kernel);

        using var document = JsonDocument.Parse(AttentionAgentReadView.RenderSummary(
            kernel.HumanInputRequests.ToArray(), [], new RenderClock(RenderedAt), goals: kernel.Goals));
        AssertSummary(document.RootElement.GetProperty("payload"), 2, 90, ordered[1].Id.Value);
    }

    [Fact]
    public void AllViews_NoOpenLandedCheck_OmitsBothKeysAndNeverHoldsLanding()
    {
        var kernel = new AgentOrchestratorKernel(new RequestClock(RenderedAt.AddSeconds(-90)));
        var goal = kernel.CreateGoal("Unlanded check");
        var wait = PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability);
        Assert.Same(wait, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));
        Assert.Empty(PostLandingOperatorCheck.HoldingLanding(kernel.GetPendingHumanInput(goal.Id)));
        Assert.NotEqual(GoalStatus.Completed, goal.Status);
        AssertFollowUpAbsent(RenderViews(kernel, new RenderClock(RenderedAt)));

        kernel = WithCompletedGoals(kernel);
        kernel.SubmitHumanInput(wait.Id, "Observed the landed behavior", origin: HumanInputAnswerOrigin.Operator);
        Assert.True(Assert.Single(kernel.HumanInputRequests).IsCompleted);
        // Pass the completed request directly: the normal pending-wait query would filter it out first.
        AssertFollowUpAbsent(RenderViews(kernel, new RenderClock(RenderedAt)));

        kernel = new AgentOrchestratorKernel(new RequestClock(RenderedAt));
        kernel.CreateGoal("No follow-up check");
        kernel = WithCompletedGoals(kernel);
        Assert.Empty(kernel.HumanInputRequests);
        AssertFollowUpAbsent(RenderViews(kernel, new RenderClock(RenderedAt)));
    }

    [Fact]
    public void Land_OriginalWaitRemainsVisibleAndAnswerableAfterGoalCompletes()
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/landed-follow-up.txt");
            var wait = PostLandingOperatorCheckTests.Request(kernel, goal, PostLandingOperatorCheck.Availability);
            var payload = wait.PlannerEvidence;
            var question = wait.Question;
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(wait.Id, Assert.Single(CliCommandHandlers.HumanWaitsForAttention(kernel, null, false)).Id);
            AssertFollowUpAbsent(kernel, goal);
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            Assert.NotEqual(mainBefore, candidate);

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced, result.Message);
            Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
            Assert.Equal(result.MergeCommitSha, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
            Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
            Assert.NotEqual(mainBefore, result.MergeCommitSha);
            kernel.CompleteGoalFromMergeEvidence(goal.Id, result.MergeCommitSha!, "Landed candidate with operator follow-up");

            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Same(wait, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));
            Assert.Equal(payload, wait.PlannerEvidence);
            Assert.Equal(question, wait.Question);
            Assert.False(wait.IsCompleted);
            Assert.Null(wait.Answer);
            foreach (var scope in new GoalId?[] { null, goal.Id })
            {
                var visible = CliCommandHandlers.HumanWaitsForAttention(kernel, scope, false);
                Assert.Same(wait, Assert.Single(visible));
                AssertFollowUpRow(AttentionAgentReadView.Render(visible, [], goals: kernel.Goals), goal, wait);
                AssertFollowUpRow(AttentionShowAgentReadView.Render(scope?.Value, visible, [], _ => null,
                    goals: kernel.Goals), goal, wait);
            }
            Assert.Equal(goal.Id, Assert.Single(SelectGoals(kernel).GoalIds));

            kernel.SubmitHumanInput(wait.Id, "Observed the required behavior on the landed build",
                origin: HumanInputAnswerOrigin.Operator);

            Assert.True(wait.IsCompleted);
            Assert.Equal("Observed the required behavior on the landed build", wait.Answer);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Assert.Empty(CliCommandHandlers.HumanWaitsForAttention(kernel, null, false));
            Assert.Empty(SelectGoals(kernel).GoalIds);
            AssertFollowUpAbsent(kernel, goal);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData(GoalStatus.Completed, "post-implementation", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Completed, "retrievable", HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Completed, null, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Completed, PostLandingOperatorCheck.Availability, HumanWaitKind.SpecClarification)]
    [InlineData(GoalStatus.Parked, PostLandingOperatorCheck.Availability, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Cancelled, PostLandingOperatorCheck.Availability, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Superseded, PostLandingOperatorCheck.Availability, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    [InlineData(GoalStatus.Failed, PostLandingOperatorCheck.Availability, HumanWaitKind.ProspectiveAcceptanceEvidence)]
    public void DefaultAttention_OtherTerminalWaitsRemainHidden(
        GoalStatus status, string? availability, HumanWaitKind kind)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Terminal attention filtering");
        var directive = Assert.Single(AgentOutputDirectives.ParseHumanInputRequest(
            PostLandingOperatorCheckTests.Directive(PostLandingOperatorCheck.Availability), AgentRole.Planner).Directives);
        kernel.RequestHumanInputDeduplicated(goal.Id, null, "post-landing wording is not evidence",
            kind: kind, evidenceOwner: "operator", plannerEvidence: availability is null ? null :
                directive.PlannerEvidence! with { Availability = availability });
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = status }]
        });
        goal = kernel.GetGoal(goal.Id);

        Assert.Equal(status, goal.Status);
        var wait = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.False(wait.IsCompleted);
        Assert.Empty(CliCommandHandlers.HumanWaitsForAttention(kernel, null, false));
        Assert.Empty(CliCommandHandlers.HumanWaitsForAttention(kernel, goal.Id, false));
        Assert.Equal(wait.Id, Assert.Single(CliCommandHandlers.HumanWaitsForAttention(kernel, goal.Id, true)).Id);
        var selection = SelectGoals(kernel);
        Assert.Empty(selection.GoalIds);
        Assert.Equal(status == GoalStatus.Parked, selection.Decline);
        AssertFollowUpAbsent(kernel, goal);
    }

    private static AttentionReadGoalSelection.Result SelectGoals(AgentOrchestratorKernel kernel) =>
        AttentionReadGoalSelection.Select(kernel.Goals.Select(goal => new GoalSummary(
                goal.Id.Value, goal.Status.ToString(), goal.Objective, DateTimeOffset.UnixEpoch.ToString("O"))).ToArray(),
            kernel.ExportSnapshot().HumanInputRequests.Where(wait => !wait.IsCompleted).ToArray(), [], []);

    private static void AssertFollowUpRow(string rendered, Goal goal, HumanInputRequest wait)
    {
        using var document = JsonDocument.Parse(rendered);
        var payload = document.RootElement.GetProperty("payload");
        Assert.Equal(wait.Id.Value, Assert.Single(payload.GetProperty("waits").EnumerateArray()).GetProperty("id").GetString());
        var row = Assert.Single(payload.GetProperty("postLandingFollowUps").EnumerateArray());
        Assert.Equal(wait.Id.Value, row.GetProperty("waitId").GetString());
        Assert.Equal(goal.Id.Value, row.GetProperty("goalId").GetString());
        Assert.Equal("landed", row.GetProperty("goalState").GetString());
        Assert.Equal(PostLandingOperatorCheck.Availability, row.GetProperty("availability").GetString());
        Assert.Equal("operator", row.GetProperty("owner").GetString());
        Assert.Equal(wait.ResumeCommand, row.GetProperty("nextAction").GetString());
    }

    private static void AssertFollowUpAbsent(AgentOrchestratorKernel kernel, Goal goal)
    {
        var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, goal.Id, false);
        AssertFollowUpAbsent(new[]
        {
            AttentionAgentReadView.Render(waits, [], goals: kernel.Goals),
            AttentionAgentReadView.RenderSummary(waits, [], goals: kernel.Goals),
            AttentionShowAgentReadView.Render(goal.Id.Value, waits, [], _ => null, goals: kernel.Goals)
        });
    }

    private static void AssertFollowUpAbsent(IEnumerable<string> views)
    {
        foreach (var rendered in views)
        {
            using var document = JsonDocument.Parse(rendered);
            Assert.False(document.RootElement.GetProperty("payload").TryGetProperty("postLandingFollowUps", out _));
            Assert.False(document.RootElement.GetProperty("payload").TryGetProperty("postLandingFollowUpSummary", out _));
        }
    }

    private static void AssertSummary(JsonElement payload, int open, long oldestAge, string oldestId)
    {
        var summary = payload.GetProperty("postLandingFollowUpSummary");
        Assert.Equal(open, summary.GetProperty("open").GetInt32());
        Assert.Equal(oldestAge, summary.GetProperty("oldestAgeSeconds").GetInt64());
        Assert.Equal(oldestId, summary.GetProperty("oldestWaitId").GetString());
    }

    private static string[] RenderViews(AgentOrchestratorKernel kernel, TimeProvider time)
    {
        var waits = kernel.HumanInputRequests.ToArray();
        return
        [
            AttentionAgentReadView.Render(waits, [], time, goals: kernel.Goals),
            AttentionAgentReadView.RenderSummary(waits, [], time, goals: kernel.Goals),
            AttentionShowAgentReadView.Render(kernel.Goals.Count == 1 ? kernel.Goals.Single().Id.Value : null,
                waits, [], _ => null, time, kernel.Goals)
        ];
    }

    private static AgentOrchestratorKernel WithCompletedGoals(AgentOrchestratorKernel kernel)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray()
        });
    }

    private sealed class RequestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class RenderClock(DateTimeOffset now, TimeSpan step = default) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            var result = _now;
            _now += step;
            return result;
        }
    }
}
