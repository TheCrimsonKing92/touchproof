using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private backlog/state files and fake process, repository and filing boundaries.
public sealed class BoardFillReselectionHostTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Landed_slice_is_rechecked_then_retired_or_filed(bool stale)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var workspace = fixture.Workspace;
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Landed slice");
        kernel.SetGoalSourceBacklogItemLink(owner.Id, fixture.Item.Id, SourceBacklogCoverage.Slice);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        { Goals = snapshot.Goals.Select(goal => goal with { Status = GoalStatus.Completed }).ToArray() });
        owner = Assert.Single(kernel.Goals);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        await repository.TransactAsync((current, _) => Task.FromResult((ShouldSave: true,
            Result: new SourceBacklogClaimStore(workspace.SqliteStatePath).EnsureClaimForNewGoal(
                current, fixture.Item.Id, owner.Id.Value, SourceBacklogCoverage.Slice))));
        Assert.False(GoalLandingPostActions.AutoCloseSourceBacklogItem(owner, workspace.BacklogStorePath,
            kernel: kernel, integrateCommitSha: Repository.Head, stateDbPath: workspace.SqliteStatePath));
        var landed = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
        Assert.Equal(BacklogItemStatus.Open, landed.Status);
        Assert.Single(landed.Notes.Where(note => note.Text.Contains("Recorded slice landing", StringComparison.Ordinal)));

        const string reason = "All remaining scope is delivered.";
        const string evidence = "docs/role-capability-matrix.md:1";
        var markdown = CliAuthorDraftCommandTests.ValidMarkdown.Replace("Observed in", "- Observed in") +
            "\nFollow-up slices: remaining scope\n";
        var json = stale
            ? JsonSerializer.Serialize(new { kind = "stale", staleKind = "delivered", reason,
                evidenceReferences = new[] { evidence }, implementingCommits = new[] { Repository.Head } })
            : JsonSerializer.Serialize(new { kind = "draft", markdown });
        var calls = 0;
        var fakeRepository = new Repository();
        var seams = new AuthorBriefDraftSeams((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new WorkerProcessRunResult(0, json, ""));
        }, fakeRepository);
        var policy = ConductorAutonomyPolicy.Permissive with
        { BoardFillMode = ConductorBoardFillMode.File, BoardFillTargetActiveGoals = 10, BoardFillMaxDraftsPerDay = 1 };
        var defaults = ConductorBoardFillHost.CreateDefault(workspace, () => policy);
        var intake = new BoardFillFilingTestFixture.IntakeFake();
        var store = new ConductorBoardFillDraftStore(ConductorBoardFillDraftStore.DefaultPath(workspace));
        var host = new ConductorBoardFillHost(store, (id, token) => AuthorBriefDraftService.Run(id,
            workspace, seams, TextWriter.Null, TextWriter.Null, token, includeBoardFillMetadata: true),
            () => BoardFillBacklogSnapshot.Read(workspace.BacklogStorePath), defaults._readiness,
            () => policy, new(workspace.ConductEventsLogPath),
            verifier: new BoardFillAssessmentTestFixture.Verifier((premise, head, _) =>
            {
                Assert.Equal(Repository.Head, head);
                Assert.Equal(1, BoardFillVerifierContract.BulletCount(premise));
                return Task.FromResult(new BoardFillPremiseVerification("complete", 1,
                    [new(1, "verified", evidence)]));
            }),
            filing: new(intake, () => new(0, new HashSet<string>()), () => Repository.Head, (_, _) => []),
            mainHead: () => Repository.Head);
        try
        {
            host.ServiceTick(kernel);
            Assert.NotNull(host.CurrentRound);
            await PanelTestHarness.Signal(host.CurrentRound!, "reselected Author round completed");
            host.ServiceTick(kernel);
            var round = Assert.Single(store.ReadAll());
            Assert.Equal(fixture.Item.Id, round.BacklogItemId);
            Assert.Equal(1, calls);
            if (stale)
            {
                Assert.Equal("stale", round.Outcome);
                var retired = (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!;
                Assert.Equal(BacklogItemStatus.Done, retired.Status);
                Assert.Contains(retired.Notes, note => note.Text.Contains(reason, StringComparison.Ordinal) &&
                    note.Text.Contains(evidence, StringComparison.Ordinal));
                Assert.Equal(1, fakeRepository.AncestryChecks);
                Assert.Empty(intake.Requests);
                Assert.Null(host.CurrentFiling);
            }
            else
            {
                Assert.Equal("draft", round.Outcome);
                Assert.True(round.Assessment!.Fileable, string.Join(", ", round.Assessment.FailingReasons));
                Assert.NotNull(host.CurrentFiling);
                await PanelTestHarness.Signal(host.CurrentFiling!, "next slice filing completed");
                host.ServiceTick(kernel);
                var request = Assert.Single(intake.Requests);
                Assert.Equal(fixture.Item.Id, request.BacklogItemId);
                Assert.Equal("slice", request.Coverage);
                Assert.Equal("slice", request.Arguments[6]);
                Assert.Single(intake.Goals);
                Assert.Equal("filed", Assert.Single(Assert.Single(store.ReadAll()).Filings!).Result);
                Assert.Equal(BacklogItemStatus.Open, (await fixture.Store.GetByExactIdAsync(fixture.Item.Id))!.Status);
            }
        }
        finally { host.Stop(); defaults.Stop(); }
    }

    private sealed class Repository : IAuthorBriefDraftRepository
    {
        internal const string Head = CliAuthorDraftCommandTests.Fixture.MainSha;
        internal int AncestryChecks { get; private set; }
        public string ResolveMainHead() => Head;
        public int? TrackedLineCount(string sha, string path) =>
            sha == Head && path == "docs/role-capability-matrix.md" ? 100 : null;
        public bool IsAncestorOfMain(string candidate, string mainHead)
        {
            Assert.Equal(Head, candidate);
            Assert.Equal(Head, mainHead);
            AncestryChecks++;
            return true;
        }
    }
}
