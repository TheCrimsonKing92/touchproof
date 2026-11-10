using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
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
    public async Task CliApprovalThroughDriverValidation_RetainsOnlyAuthorizedCandidate(bool approved)
    {
        using var fixture = new IntegrationScopeFixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Base.GitFixture.Repository);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = fixture.Goal;
        var reasonPath = Path.GetTempFileName();
        var approvedPath = approved ? "src/Other.cs" : "src/Unused.cs";
        try
        {
            File.WriteAllText(reasonPath, "Preserve the approved adjacent correction");
            string[] args = ["integration-scope", fixture.Goal.Id.Value, fixture.RoundId,
                fixture.Base.GitFixture.OriginalHead, fixture.Base.GitFixture.MainHead, "approve",
                "--text-file", reasonPath, "--idempotency-key", "cli-scope", "--path", approvedPath];
            AsyncLocalConsoleRouter.Capture(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(
                args, repository, workspace, ref agents, providers, ref profiles, ref currentGoal)));
        }
        finally { File.Delete(reasonPath); }
        var intent = Assert.Single(await fixture.Store.ListForGoalAsync(fixture.Goal.Id.Value));
        Assert.Equal(OperatorIntentVerbs.IntegrationScope, intent.Verb);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        fixture.Intents.ExecutePending(fixture.Kernel, fixture.Goal);
        fixture.Intents.CompletePersisted([fixture.Goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Store.GetAsync(intent.Id))!.Status);
        using (var journal = JsonDocument.Parse(fixture.Base.Entry.Detail!))
        {
            Assert.Equal(new[] { "src/Widget.cs" }, journal.RootElement.GetProperty("ConflictPaths")
                .EnumerateArray().Select(item => item.GetString()));
            var decision = Assert.Single(journal.RootElement.GetProperty("ScopeDecisions").EnumerateArray());
            Assert.Equal(intent.Id, decision.GetProperty("IntentId").GetString());
            var request = decision.GetProperty("Request");
            Assert.Equal(fixture.Goal.Id.Value, request.GetProperty("GoalId").GetString());
            Assert.Equal(fixture.RoundId, request.GetProperty("RoundId").GetString());
            Assert.Equal(fixture.Base.GitFixture.OriginalHead, request.GetProperty("OriginalHead").GetString());
            Assert.Equal(fixture.Base.GitFixture.MainHead, request.GetProperty("MainSha").GetString());
            Assert.Equal(new[] { approvedPath }, request.GetProperty("Paths").EnumerateArray().Select(item => item.GetString()));
        }
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
