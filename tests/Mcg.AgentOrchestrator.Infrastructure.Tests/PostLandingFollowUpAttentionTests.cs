using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private git fixtures and in-memory kernels; collection owns landing runner hooks. No console capture.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PostLandingFollowUpAttentionTests
{
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
        foreach (var rendered in new[]
        {
            AttentionAgentReadView.Render(waits, [], goals: kernel.Goals),
            AttentionShowAgentReadView.Render(goal.Id.Value, waits, [], _ => null, goals: kernel.Goals)
        })
        {
            using var document = JsonDocument.Parse(rendered);
            Assert.False(document.RootElement.GetProperty("payload").TryGetProperty("postLandingFollowUps", out _));
        }
    }
}
