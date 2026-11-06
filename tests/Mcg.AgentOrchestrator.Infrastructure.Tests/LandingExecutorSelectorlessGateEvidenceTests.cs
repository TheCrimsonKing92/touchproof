using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LandingExecutorSelectorlessGateEvidenceTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentGateReceiptRemovesHoldAndAllowsLanding(bool hasReceipt)
    {
        var repo = LandingExecutorTests.CreateGitRepository();
        try
        {
            const string changedPath = "src/selectorless-evidence.txt";
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(repo, changedPath);
            var candidate = LandingExecutorTests.ReadGit(repo, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
            Assert.NotEqual(mainBefore, candidate);
            var wait = ProspectiveEvidenceGateResolverSelectorlessTests.CreateWait(kernel, goal);

            var before = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, candidate);

            Assert.Contains(wait.Id.Value, before.HoldDescription, StringComparison.Ordinal);
            Assert.Empty(before.Resolution.ResolvedRequestIds);
            Assert.Empty(before.ObligationLines);
            Assert.Equal(wait.Id, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Id);
            string? attemptId = null;
            if (hasReceipt)
            {
                var attempt = ProspectiveEvidenceGateFixture.WriteAttempt(
                    workspace.OrchestratorDirectory, goal, candidate, []);
                attemptId = attempt.Id;
                ProspectiveEvidenceGateResolverSelectorlessTests.UpdateMetadata(
                    attempt.MetadataPath, "briefVersion", JsonValue.Create(goal.AuthoritativeBrief.Version));

                var after = LandingExecutor.EvaluatePendingHumanInput(kernel, goal, workspace, candidate);

                Assert.Null(after.HoldDescription);
                Assert.Empty(after.ObligationLines);
                Assert.Equal([wait.Id], after.Resolution.ResolvedRequestIds);
            }

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            if (hasReceipt)
            {
                Assert.True(result.MainAdvanced);
                Assert.IsNotType<LandingDecision.Escalate>(result.Decision);
                var mainAfter = LandingExecutorTests.ReadGit(repo, "rev-parse", "main");
                Assert.NotEqual(mainBefore, mainAfter);
                Assert.Equal(result.MergeCommitSha, mainAfter);
                Assert.Equal(0, GitCli.Run(repo, "merge-base", "--is-ancestor", candidate, "main").ExitCode);
                Assert.Equal("goal work", LandingExecutorTests.ReadGit(repo, "show", "main:" + changedPath));
                Assert.True(wait.IsCompleted);
                Assert.Contains(attemptId!, wait.Answer, StringComparison.Ordinal);
                Assert.Contains(candidate, wait.Answer, StringComparison.Ordinal);
                Assert.Equal(HumanInputAnswerOrigin.Worker, wait.AuthoritativeAnswer!.Origin);
                Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            }
            else
            {
                Assert.False(result.MainAdvanced);
                var escalation = Assert.IsType<LandingDecision.Escalate>(result.Decision);
                Assert.Equal(2, escalation.Decision!.Rung);
                Assert.Equal("acceptance-not-accepted", escalation.Decision.DiscriminatingEvidence);
                Assert.Contains(wait.Id.Value[..8], escalation.Reason, StringComparison.Ordinal);
                Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(repo, "rev-parse", "main"));
                Assert.False(wait.IsCompleted);
                Assert.Null(wait.Answer);
                Assert.Empty(wait.AnswerHistory);
            }
        }
        finally
        {
            LandingExecutorTests.TryDeleteDirectory(repo);
        }
    }
}
