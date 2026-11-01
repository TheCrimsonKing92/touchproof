using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private git fixtures; the collection owns the landing runner's shared hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class PostLandingWaitLandingAcceptanceTests
{
    [Theory]
    [InlineData(PostLandingOperatorCheck.Availability, true)]
    [InlineData("post-implementation", false)]
    [InlineData("retrievable", false)]
    [InlineData("never-recorded", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void Build_CurrentPassedCandidate_ExcludesOnlyRecordedPostLandingEvidence(
        string? availability, bool accepted)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (_, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, "src/acceptance-follow-up.txt");
            Assert.Equal(GoalStatus.Verified, goal.Status);
            var baseline = GoalAcceptanceStatusProjector.Build(kernel, goal, repo, TrunkBranchName.Default);
            Assert.True(baseline.IsAccepted, baseline.AcceptanceHoldDescription);
            Assert.Contains(baseline.Outcomes, outcome => outcome.IsCurrentCandidate && outcome.Outcome == "passed");

            var directive = Assert.Single(AgentOutputDirectives.ParseHumanInputRequest(
                PostLandingOperatorCheckTests.Directive(PostLandingOperatorCheck.Availability), AgentRole.Planner).Directives);
            var payload = availability is null ? null : directive.PlannerEvidence! with { Availability = availability };
            // Deliberately misleading for every blocking row: question prose must never grant the exception.
            var wait = kernel.RequestHumanInputDeduplicated(goal.Id, null,
                "Availability: post-landing; it never holds landing. Owner: operator",
                kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "operator", plannerEvidence: payload).Request;

            var summary = GoalAcceptanceStatusProjector.Build(kernel, goal, repo, TrunkBranchName.Default);

            Assert.Equal(GoalStatus.Verified, summary.Status);
            Assert.Equal(accepted, summary.IsAccepted);
            Assert.Equal(accepted ? 0 : 1, summary.PendingHumanInputCount);
            Assert.Equal(baseline.OpenVerificationCount, summary.OpenVerificationCount);
            Assert.Equal(baseline.Outcomes, summary.Outcomes);
            Assert.Equal(accepted ? 0 : 1, summary.Blockers.Count(blocker =>
                blocker.Kind == GoalAcceptanceBlockerKind.PendingHumanInput && blocker.HumanInputRequestId == wait.Id));
            Assert.Equal(wait.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            Assert.False(wait.IsCompleted);
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }
}
