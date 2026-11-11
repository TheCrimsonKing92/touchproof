using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: real SQLite and filesystem boundaries are confined to each fixture's private root.
public sealed class ExperimentReplayImportTests
{
    [Fact]
    public async Task ImportAsync_AdmittedDispatch_ImportsAndReimportsBoundedSources()
    {
        using var fixture = new ExperimentReplayTestData();
        var (workspace, kernel) = await StateAsync(fixture, 107);
        var attempt = Attempt(workspace, kernel.Goals.Single(g => g.Id.Value == "goal-000"));
        var link = Assert.IsType<AcceptanceDeveloperRoundLink>(attempt.DeveloperRoundLink);
        Assert.Equal("task-000", link.TaskId);
        Assert.Equal(Base, link.BaseCommit);
        Assert.Equal(Candidate, link.CandidateCommit);
        Assert.Equal(Main, link.ObservedMain);
        var trx = Evidence(attempt);

        // This goal is outside the top 100. Exclusive locks make accidental reads observable as diagnostics.
        var excluded = Attempt(workspace, kernel.Goals.Single(g => g.Id.Value == "goal-100"));
        var excludedTrx = Evidence(excluded);
        using var metadataLock = new FileStream(excluded.MetadataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var trxLock = new FileStream(excludedTrx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = fixture.Store();

        var imported = await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, Now, default);

        Assert.Equal(100, imported.Considered);
        Assert.Equal(100, imported.Added);
        Assert.Equal(1, imported.Replayable);
        Assert.Empty(imported.Diagnostics);
        Assert.DoesNotContain(imported.Cases, c => c.GoalId is "goal-100" or "goal-105" or "goal-106");
        var replayable = Assert.Single(imported.Cases.Where(c => c.Replayability == "replayable"));
        var gate = Assert.Single(replayable.Gates);
        Assert.Equal(attempt.AttemptId, gate.AttemptId);
        Assert.Equal("test-failed", gate.Outcome);
        Assert.Equal("Example.Tests.Fails", Assert.Single(gate.FailingTests));
        Assert.Contains(gate.Sources, s => s.Sha256 == ExperimentReplayStore.Source(trx).Sha256 && File.Exists(s.Identity));
        var evidenceRoot = Path.Combine(fixture.Root, "experiment-replay", "evidence");
        Assert.Equal(103, Directory.GetFiles(evidenceRoot).Length); // 100 round sources + metadata/result/TRX.
        Assert.Single(Directory.GetFiles(evidenceRoot, "*.trx"));
        Assert.True(File.Exists(trx)); // Import never moves/deletes shared gate evidence.
        var repeated = await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, Now, default);
        Assert.Equal(0, repeated.Added);
        Assert.Equal(imported.CorpusIdentity, repeated.CorpusIdentity);
        Assert.Equal(103, Directory.GetFiles(evidenceRoot).Length);
    }

    [Theory]
    [InlineData("missing", "unsupported-history:")]
    [InlineData("task", "unsupported-provenance:")]
    [InlineData("dispatch", "unsupported-provenance:")]
    [InlineData("base", "unsupported-provenance:")]
    [InlineData("main", "unsupported-provenance:")]
    public async Task ImportAsync_UnlinkedHistory_DoesNotReadResultsOrCopyTrx(string mismatch, string reason)
    {
        using var fixture = new ExperimentReplayTestData();
        var (workspace, kernel) = await StateAsync(fixture, 1);
        var attempt = Attempt(workspace, Assert.Single(kernel.Goals));
        var trx = Evidence(attempt);
        AcceptanceDeveloperRoundLink? link = Assert.IsType<AcceptanceDeveloperRoundLink>(attempt.DeveloperRoundLink);
        link = mismatch switch
        {
            "missing" => null,
            "task" => link with { TaskId = "other-task" },
            "dispatch" => link with { DispatchedAt = link.DispatchedAt.AddSeconds(-1) },
            "base" => link with { BaseCommit = Main },
            _ => link with { ObservedMain = Base }
        };
        attempt = attempt with { DeveloperRoundLink = link, TestResultPaths = [trx] };
        File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, ExperimentReplayStore.Json));
        using var resultLock = new FileStream(attempt.ResultPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var trxLock = new FileStream(trx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = fixture.Store();

        var imported = await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, Now, default);

        Assert.Equal(0, imported.Replayable);
        var row = Assert.Single(imported.Cases);
        Assert.Equal("unknown", row.Replayability);
        Assert.Empty(row.Gates);
        Assert.StartsWith(reason, Assert.Single(row.Reasons));
        Assert.Contains(reason, Assert.Single(imported.Diagnostics));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "experiment-replay", "evidence")));
        Assert.Equal(0, (await ExperimentReplayCorpusImporter.ImportAsync(workspace, store, Now, default)).Added);
    }

    [Fact]
    public async Task ImportAsync_NoSubsequentGate_DistinguishesMissingGateFromUnsupportedHistory()
    {
        using var fixture = new ExperimentReplayTestData();
        var (workspace, _) = await StateAsync(fixture, 1);
        var imported = await ExperimentReplayCorpusImporter.ImportAsync(workspace, fixture.Store(), Now, default);
        Assert.Equal("no exact-linked subsequent solo gate", Assert.Single(Assert.Single(imported.Cases).Reasons));
        Assert.Empty(imported.Diagnostics);
    }

    [Fact]
    public async Task ImportAsync_ChangedResultCandidate_DoesNotReadTrxOrPromoteCase()
    {
        using var fixture = new ExperimentReplayTestData();
        var (workspace, kernel) = await StateAsync(fixture, 1);
        var attempt = Attempt(workspace, Assert.Single(kernel.Goals));
        var trx = Evidence(attempt);
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new {
            branchHeadSha = Main, mainHeadSha = Main, acceptance = new { passed = false, testResultPaths = new[] { trx } }
        }));
        using var trxLock = new FileStream(trx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var imported = await ExperimentReplayCorpusImporter.ImportAsync(workspace, fixture.Store(), Now, default);

        Assert.Equal(0, imported.Replayable);
        Assert.Empty(Assert.Single(imported.Cases).Gates);
        Assert.Contains("result identity disagrees", Assert.Single(imported.Diagnostics));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "experiment-replay", "evidence")));
    }

    [Fact]
    public async Task Capture_ChangedCandidateOrMissingMain_DoesNotAssertDispatchLinkage()
    {
        using var fixture = new ExperimentReplayTestData();
        var (_, kernel) = await StateAsync(fixture, 1);
        var candidate = CandidateFor(Assert.Single(kernel.Goals));
        Assert.NotNull(AcceptanceDeveloperRoundLink.Capture(candidate));
        Assert.Null(AcceptanceDeveloperRoundLink.Capture(candidate with { BranchHeadSha = Main }));
        Assert.Null(AcceptanceDeveloperRoundLink.Capture(candidate with { MainHeadSha = Base }));
        var snapshot = kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        var task = Assert.Single(goal.Tasks);
        var dispatch = Assert.Single(task.DispatchHistory!);
        var missingMain = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with {
            Tasks = [task with { DispatchHistory = [dispatch with { PreDispatchIntegrationReceipt = null }] }] }] });
        Assert.Null(AcceptanceDeveloperRoundLink.Capture(CandidateFor(Assert.Single(missingMain.Goals))));
        var duplicate = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [goal with {
            Tasks = [task with { DispatchHistory = [dispatch, dispatch] }] }] });
        Assert.Null(AcceptanceDeveloperRoundLink.Capture(CandidateFor(Assert.Single(duplicate.Goals))));
    }

    private static ConductorParallelAcceptanceCandidate CandidateFor(Goal goal) => new(goal, 0, [], [], [], [], Candidate, Main);

    private static ConductorParallelAcceptanceAttempt Attempt(OrchestratorWorkspace workspace, Goal goal)
    {
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), TrunkBranchName.Default,
            utcNow: () => Now.AddSeconds(-10), projectName: workspace.ProjectName);
        // This is the owning admission path, not a hand-authored replay link.
        return coordinator.CreateAttemptForTests(CandidateFor(goal));
    }

    private static string Evidence(ConductorParallelAcceptanceAttempt attempt)
    {
        var trx = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath)!, "tests.trx");
        File.WriteAllText(trx, "<TestRun><UnitTestResult testId='1' testName='Fails' outcome='Failed'/>" +
            "<TestDefinitions><UnitTest id='1'><TestMethod className='Example.Tests' name='Fails'/></UnitTest></TestDefinitions></TestRun>");
        File.WriteAllText(attempt.ResultPath, JsonSerializer.Serialize(new {
            branchHeadSha = Candidate, mainHeadSha = Main, acceptance = new { passed = false, testResultPaths = new[] { trx } }
        }));
        return trx;
    }

    private static async Task<(OrchestratorWorkspace Workspace, AgentOrchestratorKernel Kernel)> StateAsync(
        ExperimentReplayTestData fixture, int count)
    {
        var snapshots = Enumerable.Range(0, count).Select(i =>
        {
            var goal = $"goal-{i:D3}";
            var task = $"task-{i:D3}";
            var ended = i == 105 ? Now.AddDays(-31) : i == 106 ? Now.AddDays(1) : Now.AddMinutes(-i - 1);
            var dispatched = ended.AddMinutes(-1);
            var dispatch = new TaskDispatchSnapshot("worker", "command", fixture.Root, dispatched,
                BaseCommit: Base, ResultCommit: Candidate,
                PreDispatchIntegrationReceipt: new(new GoalId(goal), new TaskId(task), dispatched.AddMinutes(-1), Main, Main, Base));
            return new GoalSnapshot(goal, "Import historical round", GoalStatus.Active,
                [new TaskSnapshot(task, "Implement", AgentRole.Developer, WorkTaskStatus.Completed,
                    null, null, null, [], null, null, DispatchHistory: [dispatch])],
                [new ProgressEventSnapshot(goal, task, ProgressKind.TaskCompleted, "completed", ended)]);
        }).ToArray();
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
        var state = Path.Combine(fixture.Root, "state.db");
        StateDbMigrations.EnsureUpToDate(state);
        await new SqliteOrchestratorStateRepository(state).SaveAsync(kernel);
        return (new(fixture.Root, fixture.Root, fixture.Root, "home", true, "default", false,
            state, "unused-agents", "unused-workers", "unused-prompts", "unused-logs", "unused-continuation", "unused-transcript"), kernel);
    }
}
