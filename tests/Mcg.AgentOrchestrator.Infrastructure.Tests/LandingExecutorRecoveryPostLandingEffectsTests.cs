using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: each repository, database and event log is private; git overrides are flow-local.
public sealed class LandingExecutorRecoveryPostLandingEffectsTests : Xunit.IClassFixture<IsolatedDotnetRootFixture>
{
    [Fact]
    public async Task SynchronizationFailure_NextLandingCallRecovers_EmitsOneGoalLandedAndReleasesHoldsOnce()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            const string landedPath = "src/recovered-effects.txt";
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, landedPath);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var goalRevision = LandingExecutorTests.ReadGit(root, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(root, "rev-parse", "main");
            Assert.NotEqual(mainBefore, goalRevision);
            var hold = RecordHold(workspace, goal, "original");
            var realRunner = LandingExecutor.GitRunner;
            var synchronizationAttempted = false;
            using (LandingExecutor.UseGitRunner((directory, args) =>
            {
                if (args[0] != "read-tree") return realRunner(directory, args);
                synchronizationAttempted = true;
                return new GitCli.GitResult(1, "", "forced checkout failure");
            }))
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer));
                Assert.Contains("forced checkout failure", error.Message, StringComparison.Ordinal);
            }

            Assert.True(synchronizationAttempted, "The first landing must reach checkout synchronization.");
            var intent = GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(root, goal.Id));
            Assert.NotNull(intent);
            // Landing prepares a --no-ff merge; its durable intent identifies the candidate, not the goal tip.
            var candidate = intent.MergeCommitSha;
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(root, "rev-parse", $"{candidate}^1"));
            Assert.Equal(goalRevision, LandingExecutorTests.ReadGit(root, "rev-parse", $"{candidate}^2"));
            Assert.Equal(candidate, LandingExecutorTests.ReadGit(root, "rev-parse", "main"));
            Assert.Equal(LandingExecutorTests.ReadGit(root, "rev-parse", $"{mainBefore}^{{tree}}"),
                LandingExecutorTests.ReadGit(root, "write-tree"));
            Assert.False(File.Exists(Path.Combine(root, landedPath)));
            Assert.Empty(ReadLanded(workspace, goal));
            Assert.Empty(ReadClearances(workspace, goal));
            Assert.Contains(OpenHolds(kernel, workspace, goal), item => item.Id == hold.Id);

            // The normal landing guard rejects tracked edits; recovery must preserve this later edit.
            const string localReadme = "preserved local edit\n";
            File.WriteAllText(Path.Combine(root, "README.md"), localReadme);
            var recovered = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);

            Assert.IsType<LandingDecision.Promote>(recovered.Decision);
            Assert.True(recovered.MainAdvanced, recovered.Message);
            Assert.Equal(candidate, recovered.MergeCommitSha);
            AssertRecoveredEffects(workspace, kernel, goal, candidate, hold.Id);
            Assert.Equal(candidate, LandingExecutorTests.ReadGit(root, "rev-parse", "main"));
            Assert.Equal("main", LandingExecutorTests.ReadGit(root, "branch", "--show-current"));
            Assert.Equal(LandingExecutorTests.ReadGit(root, "rev-parse", "main^{tree}"),
                LandingExecutorTests.ReadGit(root, "write-tree"));
            Assert.Equal(0, GitCli.Run(root, "diff", "--cached", "--quiet", "main", "--").ExitCode);
            var committedFile = GitCli.Run(root, "show", $"main:{landedPath}");
            Assert.True(committedFile.Succeeded, committedFile.Error);
            Assert.Equal(committedFile.Output.ReplaceLineEndings("\n"),
                File.ReadAllText(Path.Combine(root, landedPath)).ReplaceLineEndings("\n"));
            Assert.Equal(0, GitCli.Run(root, "diff", "--quiet", "main", "--", landedPath).ExitCode);
            Assert.Equal(localReadme, File.ReadAllText(Path.Combine(root, "README.md")));
            Assert.Equal("README.md", LandingExecutorTests.ReadGit(root, "diff", "--name-only"));

            var sentinel = RecordHold(workspace, goal, "after-recovery");
            var later = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);
            Assert.IsType<LandingDecision.Promote>(later.Decision);
            Assert.False(later.MainAdvanced);
            Assert.Single(ReadLanded(workspace, goal));
            Assert.Single(ReadClearances(workspace, goal));
            Assert.Equal(sentinel.Id, Assert.Single(OpenHolds(kernel, workspace, goal)).Id);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstAttemptLanding_LaterLandingCall_DoesNotRepeatGoalLandedOrHoldRelease(bool recordConductorLandCompleted)
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/first-attempt.txt");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var goalRevision = LandingExecutorTests.ReadGit(root, "rev-parse", GoalWorktrees.BranchName(goal.Id));
            var mainBefore = LandingExecutorTests.ReadGit(root, "rev-parse", "main");
            var hold = RecordHold(workspace, goal, "original");

            var first = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);

            Assert.IsType<LandingDecision.Promote>(first.Decision);
            Assert.True(first.MainAdvanced, first.Message);
            var intent = GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(root, goal.Id));
            Assert.NotNull(intent);
            var candidate = intent.MergeCommitSha;
            Assert.Equal(mainBefore, LandingExecutorTests.ReadGit(root, "rev-parse", $"{candidate}^1"));
            Assert.Equal(goalRevision, LandingExecutorTests.ReadGit(root, "rev-parse", $"{candidate}^2"));
            Assert.Equal(candidate, first.MergeCommitSha);
            var landed = Assert.Single(ReadLanded(workspace, goal));
            Assert.Equal(candidate, landed.GetProperty("admissionCandidateSha").GetString());
            Assert.Equal("green-gate-auto", landed.GetProperty("admissionRule").GetString());
            Assert.Contains(hold.Id, Assert.Single(ReadClearances(workspace, goal)), StringComparison.Ordinal);
            Assert.Empty(OpenHolds(kernel, workspace, goal));
            LandingExecutorCheckoutSynchronizationTests.AssertCheckoutAtMain(root);
            Assert.False(GoalOperationJournal.HasCompletedLandingEvidence(GoalOperationJournal.Read(root, goal.Id)));
            if (recordConductorLandCompleted)
                GoalOperationJournal.Completed(root, goal, "conductor:land", first.Message);
            var sentinel = RecordHold(workspace, goal, "after-first-attempt");

            var later = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);

            Assert.IsType<LandingDecision.Promote>(later.Decision);
            Assert.Equal(!recordConductorLandCompleted, later.MainAdvanced);
            if (!recordConductorLandCompleted)
                Assert.Null(GoalOperationJournal.TryGetLatestLandingIntent(GoalOperationJournal.Read(root, goal.Id)));
            Assert.Equal(candidate, Assert.Single(ReadLanded(workspace, goal)).GetProperty("admissionCandidateSha").GetString());
            Assert.Contains(hold.Id, Assert.Single(ReadClearances(workspace, goal)), StringComparison.Ordinal);
            Assert.Equal(sentinel.Id, Assert.Single(OpenHolds(kernel, workspace, goal)).Id);
            LandingExecutorCheckoutSynchronizationTests.AssertCheckoutAtMain(root);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task Recovery_PriorLandingForAnotherCandidate_DoesNotSuppressEffects()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var (workspace, kernel, goal) = LandingExecutorTests.CreateAcceptedCandidate(root, "src/new-candidate.txt");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var priorMain = LandingExecutorTests.ReadGit(root, "rev-parse", "main");
            var branch = GoalWorktrees.BranchName(goal.Id);
            var candidate = LandingExecutorTests.ReadGit(root, "rev-parse", branch);
            Assert.NotEqual(priorMain, candidate);
            writer.AppendGoalLanded(goal.Id, LandingExecutor.IntegrationBranchName, branch,
                LandingAdmissionReceipt.Recovered(priorMain));
            Assert.True(GitCli.Run(root, "update-ref", "refs/heads/main", candidate, priorMain).Succeeded);
            GoalOperationJournal.RecordLandingIntent(root, goal, branch, LandingExecutor.IntegrationBranchName,
                candidate, "fixture interrupted landing");
            var hold = RecordHold(workspace, goal, "new-candidate");

            var recovered = LandingExecutor.Execute(kernel, goal, workspace, eventWriter: writer);

            Assert.IsType<LandingDecision.Promote>(recovered.Decision);
            Assert.True(recovered.MainAdvanced, recovered.Message);
            var events = ReadLanded(workspace, goal);
            Assert.Equal(2, events.Length);
            var landed = Assert.Single(events.Where(item => item.GetProperty("admissionCandidateSha").GetString() == candidate));
            Assert.Equal("recovered-landing", landed.GetProperty("admissionRule").GetString());
            Assert.Contains(hold.Id, Assert.Single(ReadClearances(workspace, goal)), StringComparison.Ordinal);
            Assert.Empty(OpenHolds(kernel, workspace, goal));
            LandingExecutorCheckoutSynchronizationTests.AssertCheckoutAtMain(root);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void AssertRecoveredEffects(OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel,
        Goal goal, string candidate, string holdId)
    {
        var landed = Assert.Single(ReadLanded(workspace, goal));
        Assert.Equal(candidate, landed.GetProperty("admissionCandidateSha").GetString());
        Assert.Equal("recovered-landing", landed.GetProperty("admissionRule").GetString());
        Assert.Contains(holdId, Assert.Single(ReadClearances(workspace, goal)), StringComparison.Ordinal);
        Assert.Empty(OpenHolds(kernel, workspace, goal));
    }

    private static OperatorInboxItem RecordHold(OrchestratorWorkspace workspace, Goal goal, string key)
    {
        var task = goal.Tasks.First();
        return Assert.Single(OperatorInbox.RecordOwnershipHolds(workspace, goal,
            [new OwnershipHoldRequest(task.Id, 1, task.RequiredRole, [$"src/{key}.txt"], key)]));
    }

    private static OperatorInboxItem[] OpenHolds(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, Goal goal) =>
        OperatorInbox.Build(kernel, [], WorkerProfileCatalog.Default(), workspace, goal.Id.Value[..8]).Items
            .Where(item => item.Kind == OperatorInboxKind.OwnershipHold && item.GoalId == goal.Id.Value).ToArray();

    private static JsonElement[] ReadLanded(OrchestratorWorkspace workspace, Goal goal)
    {
        var path = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).Where(item => item.GetProperty("eventType").GetString() == "GoalLanded").ToArray();
    }

    private static string[] ReadClearances(OrchestratorWorkspace workspace, Goal goal) =>
        File.Exists(workspace.ConductEventsLogPath)
            ? File.ReadLines(workspace.ConductEventsLogPath)
                .Where(line => line.Contains($"OWNERSHIP_HOLD_CLEARED goal={goal.Id.Value[..8]}", StringComparison.Ordinal)).ToArray()
            : [];
}
