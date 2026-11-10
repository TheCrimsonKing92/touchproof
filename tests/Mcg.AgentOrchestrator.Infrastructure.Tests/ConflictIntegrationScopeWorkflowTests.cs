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
    public async Task AnsweredEndedAttempt_RecapturesOnlyApprovedScope(bool approved)
    {
        using var fixture = new IntegrationScopeFixture();
        var git = fixture.Base.GitFixture;
        var task = fixture.Base.Developer;
        Assert.True(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var retryAt = task.LatestRetryAt;
        var process = StartAttempt(fixture);
        var ended = process with { CompletedAt = process.StartedAt.AddSeconds(1), ExitCode = 0 };
        // Apply the completion as the process refresher does. A bare exit record leaves
        // dispatch held for reconciliation before it can reach integration preflight.
        fixture.Kernel.RecordTaskProcessRefreshed(fixture.Goal.Id, task.Id,
            ended, new TaskVerificationRecord(process.Command, process.WorkingDirectory, 0,
                "HUMAN_INPUT: May I edit the adjacent seam?", "", ended.CompletedAt!.Value,
                HumanInputQuestion: "May I edit the adjacent seam?", AssignedScopeComplete: false));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(task, ended));
        var question = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Git(git.Worktree, "merge", "--abort");
        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, question.Id.Value,
            fixture.Goal.Id.Value, "Approved: edit src/Other.cs", OperatorActorKind.Human);
        var answer = fixture.Intent() with { Verb = OperatorIntentVerbs.Answer,
            PayloadJson = JsonSerializer.Serialize(payload, OperatorIntentJson.Options) };
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(answer)).Status);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(retryAt, task.LatestRetryAt);

        var developerStarts = 0;
        var testerStarts = 0;
        var escalations = new List<string>();
        string? candidate = null;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Git(git.Worktree, "rev-parse", "HEAD").Trim()),
            dispatchAndStart: _ =>
            {
                if (task.Status == WorkTaskStatus.Assigned)
                {
                    developerStarts++;
                    Assert.True(ConductorConflictIntegrationMarker.Admits(task, git.Worktree));
                    candidate = fixture.Base.Resolve(outside: true);
                    PassVerification(fixture.Kernel, fixture.Goal, task, hasCommittedChanges: true);
                }
                else testerStarts++;
                return DispatchStartOutcome.Started();
            }, writeEscalation: (_, _, reason) => escalations.Add(reason), executionDirectory: git.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());
        driver.OverrideDeveloperCompletionStructuralPreflightForTests(_ => new(false, "fixture structural check"), TimeSpan.FromSeconds(30));
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        // Before the fix the old admission bypasses ScopeRequired and starts another worker.
        Assert.Equal(0, developerStarts);
        var requirement = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        git.AssertRestored();
        var restarted = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        Assert.False(fixture.Reload(restarted).IntegrateMainBeforeDeveloperDispatch(restarted.GetGoal(fixture.Goal.Id)).CanDispatch);
        Assert.Equal(requirement.Id, Assert.Single(restarted.HumanInputRequests.Where(item => !item.IsCompleted)).Id);
        Assert.Collection(escalations,
            reason => Assert.Equal(requirement.Question, reason),
            reason => Assert.Equal("Goal is in AwaitingHumanInput state; operator action required", reason));
        // The scope wait above is expected. Observe settlement escalations separately so
        // approval must proceed cleanly and an unapproved edit must fail exactly once.
        escalations.Clear();
        var approval = fixture.Intent(paths: approved ? ["src/Other.cs"] : ["src/Unused.cs"]);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(approval)).Status);
        Assert.True(requirement.IsCompleted);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.Equal(1, developerStarts);
        if (approved)
        {
            Assert.Empty(escalations);
            Assert.Equal(1, testerStarts);
            Assert.Equal(candidate, Git(git.Worktree, "rev-parse", "HEAD").Trim());
            Assert.Equal(GoalOperationStatus.Completed, fixture.Base.Entry.Status);
            using var journal = JsonDocument.Parse(fixture.Base.Entry.Detail!);
            var admission = journal.RootElement.GetProperty("Admission");
            Assert.Equal(task.DispatchHistory.Count, admission.GetProperty("DispatchOrdinal").GetInt32());
            Assert.Equal(new[] { approval.Id }, admission.GetProperty("ApprovalIds").EnumerateArray().Select(item => item.GetString()));
            Assert.Null(fixture.Reload().Validate(fixture.Goal));
        }
        else
        {
            Assert.Contains("src/Other.cs", Assert.Single(escalations));
            Assert.Equal(0, testerStarts);
            git.AssertRestored();
        }
    }

    [Fact]
    public async Task AdmittedRetryWithMovedMain_RetiresRevokedScopeWait()
    {
        using var fixture = new IntegrationScopeFixture();
        var git = fixture.Base.GitFixture;
        var approval = fixture.Intent();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(approval)).Status);
        Assert.True(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var process = StartAttempt(fixture);
        // Cancellation settles the task as well as the process before the same-round retry.
        fixture.Kernel.RecordTaskProcessCancelled(fixture.Goal.Id, fixture.Base.Developer.Id,
            process with { CompletedAt = process.StartedAt.AddSeconds(1), ExitCode = 1, WasCancelled = true });
        Assert.Equal(WorkTaskStatus.Cancelled, fixture.Base.Developer.Status);
        Assert.False(fixture.Base.Developer.LastProcess!.IsRunning);
        Git(git.Worktree, "merge", "--abort");
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Base.Developer.Id, "Retry ended integration",
            RetryCause.MainDriftConflict, authoritativeRetryFeedback: true);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(fixture.Intent(
            IntegrationScopeAction.Revoke, approvalId: approval.Id))).Status);
        Assert.False(fixture.Reload().IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var requirement = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        File.WriteAllText(Path.Combine(git.Repository, "src/MainLater.cs"), "later main\n");
        Commit(git.Repository, "Advance main after an admitted attempt ended");
        var starts = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            executionDirectory: git.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        // Before the fix Admitted remains true and the obsolete requirement never resolves.
        Assert.True(requirement.IsCompleted);
        Assert.Contains("no path authority transferred", requirement.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Worker, requirement.AuthoritativeAnswer!.Origin);
        Assert.Equal(GoalOperationStatus.Failed, fixture.Base.Entry.Status);
        Assert.NotEqual(GoalStatus.WaitingForHuman, fixture.Goal.Status);
        Assert.Equal(0, starts);
        git.AssertRestored();
        fixture.Reload().ApplyScope(fixture.Goal, approval);
        Assert.Equal(GoalOperationStatus.Failed, fixture.Base.Entry.Status);
        Assert.Throws<InvalidOperationException>(() => fixture.Reload().ApplyScope(fixture.Goal, fixture.Intent()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveAttempt_MainDriftAndRevocationPreserveCapturedScope(bool legacy)
    {
        using var fixture = new IntegrationScopeFixture();
        var git = fixture.Base.GitFixture;
        var approval = fixture.Intent();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(approval)).Status);
        Assert.True(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        StartAttempt(fixture);
        if (legacy)
        {
            var detail = System.Text.Json.Nodes.JsonNode.Parse(fixture.Base.Entry.Detail!)!;
            detail["Admission"]!.AsObject().Remove("DispatchOrdinal");
            GoalOperationJournal.Begin(git.Repository, fixture.Goal,
                ConductorConflictIntegration.OperationName, detail.ToJsonString());
        }
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(fixture.Intent(
            IntegrationScopeAction.Revoke, approvalId: approval.Id))).Status);
        File.WriteAllText(Path.Combine(git.Repository, "src/MainLater.cs"), "later main\n");
        Commit(git.Repository, "Advance main during the admitted attempt");
        var restarted = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var goal = restarted.GetGoal(fixture.Goal.Id);
        var owner = fixture.Reload(restarted);
        Assert.False(owner.TryRecord(goal).Started);
        Assert.Equal(GoalOperationStatus.Begin, fixture.Base.Entry.Status);
        var candidate = fixture.Base.Resolve(outside: true);
        Assert.Null(owner.Validate(goal));
        Assert.Equal(candidate, Git(git.Worktree, "rev-parse", "HEAD").Trim());
        Assert.Equal(GoalOperationStatus.Completed, fixture.Base.Entry.Status);
    }

    private static TaskProcessRecord StartAttempt(IntegrationScopeFixture fixture)
    {
        var task = fixture.Base.Developer;
        var worktree = fixture.Base.GitFixture.Worktree;
        DispatchTask(fixture.Kernel, fixture.Goal, task, workingDirectory: worktree);
        var process = new TaskProcessRecord(12345, "test.exe", worktree, Path.Combine(worktree, "out.log"),
            Path.Combine(worktree, "err.log"), Path.Combine(worktree, "exit.txt"), task.LastDispatch!.DispatchedAt, null, null);
        fixture.Kernel.RecordTaskProcessStarted(fixture.Goal.Id, task.Id, process);
        return process;
    }

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

    [Theory]
    [InlineData("main")]
    [InlineData("head")]
    [InlineData("terminal")]
    public void ObsoleteScopeThroughDriver_RetiresWaitWithoutStartingWorker(string change)
    {
        using var fixture = new IntegrationScopeFixture();
        fixture.Owner.RequireScopeAfterAnswer(fixture.Goal, fixture.Base.Developer.Id.Value);
        Assert.False(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var requirement = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.Equal(GoalStatus.WaitingForHuman, fixture.Goal.Status);
        var git = fixture.Base.GitFixture;
        if (change == "main")
        {
            File.WriteAllText(Path.Combine(git.Repository, "src/MainLater.cs"), "later main\n");
            Commit(git.Repository, "Advance main while scope is pending");
        }
        else if (change == "head")
        {
            File.WriteAllText(Path.Combine(git.Worktree, "src/Other.cs"), "operator recovery\n");
            Commit(git.Worktree, "Operator recovery while scope is pending");
        }
        else GoalOperationJournal.Failed(git.Repository, fixture.Goal,
            ConductorConflictIntegration.OperationName, fixture.Base.Entry.Detail);
        var expectedHead = Git(git.Worktree, "rev-parse", "HEAD").Trim();
        var starts = 0;
        var escalations = new List<string>();
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, reason) => escalations.Add(reason), executionDirectory: git.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());

        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(requirement.IsCompleted);
        Assert.Contains("no path authority transferred", requirement.Answer);
        Assert.Equal(HumanInputAnswerOrigin.Worker, requirement.AuthoritativeAnswer!.Origin);
        Assert.NotEqual(GoalStatus.WaitingForHuman, fixture.Goal.Status);
        Assert.Equal(0, starts);
        Assert.Contains("conflict", Assert.Single(escalations));
        Assert.Equal(GoalOperationStatus.Failed, fixture.Base.Entry.Status);
        Assert.Equal(expectedHead, Git(git.Worktree, "rev-parse", "HEAD").Trim());
        Assert.False(GitCli.IsWorktreeDirty(git.Worktree));
        Assert.Equal(1, GitCli.Run(git.Worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD").ExitCode);
        var historyCount = GoalOperationJournal.Read(git.Repository, fixture.Goal.Id).Entries.Count;
        fixture.Reload().ReconcileScopeRequirements(fixture.Goal);
        Assert.Equal(historyCount, GoalOperationJournal.Read(git.Repository, fixture.Goal.Id).Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRoundThroughDriver_RequiresFreshTypedDecision(bool terminal)
    {
        using var fixture = new IntegrationScopeFixture();
        var oldApproval = fixture.Intent();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(oldApproval)).Status);
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Apply(fixture.Intent(
            IntegrationScopeAction.Revoke, approvalId: oldApproval.Id))).Status);
        Assert.False(fixture.Owner.IntegrateMainBeforeDeveloperDispatch(fixture.Goal).CanDispatch);
        var obsolete = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        var detail = fixture.Base.Entry.Detail!;
        if (terminal) GoalOperationJournal.Failed(fixture.Base.GitFixture.Repository, fixture.Goal,
            ConductorConflictIntegration.OperationName, detail);
        var replacementId = Guid.NewGuid().ToString("N");
        var replacement = System.Text.Json.Nodes.JsonNode.Parse(detail)!;
        replacement["RoundId"] = replacementId;
        replacement["ScopeDecisions"] = null;
        GoalOperationJournal.Begin(fixture.Base.GitFixture.Repository, fixture.Goal,
            ConductorConflictIntegration.OperationName, replacement.ToJsonString());
        // An ordinary question with the same text must not be treated as the typed requirement.
        var ordinary = fixture.Kernel.RequestHumanInput(fixture.Goal.Id, null, obsolete.Question);
        fixture.Owner.ReconcileScopeRequirements(fixture.Goal);
        Assert.True(obsolete.IsCompleted);
        Assert.False(ordinary.IsCompleted);
        fixture.Kernel.SubmitHumanInput(ordinary.Id, "Unrelated question answered", supersedeSiblings: false);

        var starts = 0;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            executionDirectory: fixture.Base.GitFixture.Repository);
        driver.ConfigureDeveloperCompletionStructuralPreflightRetry(fixture.Kernel, fixture.Reload());
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        var current = Assert.Single(fixture.Kernel.HumanInputRequests.Where(item => !item.IsCompleted));
        Assert.Equal("integration-scope:" + replacementId, current.QuestionFingerprint);
        // Replaying the old approval must neither resurrect its grant nor answer the new wait.
        fixture.Reload().ApplyScope(fixture.Goal, oldApproval);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.False(current.IsCompleted);
        Assert.Equal(0, starts);
        fixture.Base.GitFixture.AssertRestored();

        Assert.Equal(OperatorIntentStatus.Applied,
            (await fixture.Apply(fixture.Intent(IntegrationScopeAction.OriginalOnly))).Status);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative);
        Assert.True(current.IsCompleted);
        Assert.Equal(1, starts);
        fixture.Base.Resolve(outside: true);
        Assert.Contains("src/Other.cs", fixture.Reload().Validate(fixture.Goal));
        fixture.Base.GitFixture.AssertRestored();
    }
}
