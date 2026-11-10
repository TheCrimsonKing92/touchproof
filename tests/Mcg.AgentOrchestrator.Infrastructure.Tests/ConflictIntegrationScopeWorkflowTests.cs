using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AdditiveConflictGitFixture;
using static ConductorDriverTests;

// Parallel-safe: actual intent application and driver workflow with private Git, injected workers/builds.
public sealed class ConflictIntegrationScopeWorkflowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OperatorIntentThroughDriverValidation_RetainsOnlyAuthorizedCandidate(bool approved)
    {
        using var fixture = new IntegrationScopeFixture();
        var intent = fixture.Intent(paths: [approved ? "src/Other.cs" : "src/Unused.cs"]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(intent)).Status);
        var developerStarts = 0;
        var testerStarts = 0;
        var acceptanceRuns = 0;
        string? head = null;
        var escalations = new List<string>();
        var git = fixture.Base.GitFixture;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Git(git.Worktree, "rev-parse", "HEAD").Trim()),
            dispatchAndStart: _ =>
            {
                if (fixture.Base.Developer.Status == WorkTaskStatus.Assigned)
                {
                    developerStarts++;
                    Assert.True(ConductorConflictIntegrationMarker.Admits(fixture.Base.Developer, git.Worktree));
                    head = fixture.Base.Resolve(outside: true);
                    PassVerification(fixture.Kernel, fixture.Goal, fixture.Base.Developer, hasCommittedChanges: true);
                }
                else testerStarts++;
                return DispatchStartOutcome.Started();
            },
            rebaseOntoMain: goal => RatchetConflictDispatchIntegration.MergeMainBeforeLanding(fixture.Kernel, git.Repository,
                goal, ConductorDriver.CreateAdditiveConflictMergeOptions(fixture.Kernel, goal, Path.Combine(git.Repository, "events.log")), "main"),
            runAcceptance: _ => { acceptanceRuns++; return true; },
            writeEscalation: (_, _, reason) => escalations.Add(reason), executionDirectory: git.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());
        driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new(false, "fixture structural check"), TimeSpan.FromSeconds(30));
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal(1, developerStarts);
        if (approved)
        {
            Assert.Empty(escalations);
            Assert.Equal(1, testerStarts);
            Assert.Equal(head, Git(git.Worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(GoalOperationStatus.Completed, fixture.Base.Entry.Status);
            PassVerification(fixture.Kernel, fixture.Goal, fixture.Base.Tester);
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal(1, acceptanceRuns);
            Assert.Equal(1, developerStarts);
        }
        else
        {
            Assert.Contains("src/Other.cs", Assert.Single(escalations));
            Assert.Equal(0, testerStarts);
            Assert.Equal(0, acceptanceRuns);
            git.AssertRestored();
        }
    }

    [Fact]
    public void UnresolvedScopeThroughDriver_StartsNoWorker()
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.RequireScopeAfterAnswer(fixture.Goal, fixture.Base.Developer.Id.Value);
        var starts = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            executionDirectory: fixture.Base.GitFixture.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal(0, starts);
        Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        fixture.Base.GitFixture.AssertRestored();
    }
}
