using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Private SQLite workspace; the collection owns the process environment cleared by the CLI fixture.
[Collection(TestCollections.CliProcessEnvironment)]
public sealed class BoardFillReselectionFilingTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Superseded)]
    public async Task Filing_transfers_only_a_terminal_owners_claim(GoalStatus status)
    {
        var root = Directory.CreateTempSubdirectory("board-fill-takeover-").FullName;
        try
        {
            using var sandbox = ClearWorkerSandboxEnv();
            var workspace = CreateRefinedWorkspace(root);
            AgentCatalogStore.Save(workspace.AgentCatalogPath, AgentCatalog.Default());
            WorkerProfileStore.Save(workspace.WorkerProfilePath, WorkerProfileCatalog.Default());
            var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("File remaining slice");
            var path = Path.Combine(root, "draft.md");
            File.WriteAllText(path, BoardFillAssessmentTestFixture.Brief);
            var adapter = new BoardFillCliGoalIntake(workspace);
            var first = adapter.File(new("first", path, item.Id, "slice"), default);
            Assert.Equal("filed", first.Kind);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var kernel = await repository.LoadAsync();
            var owner = Assert.Single(kernel.Goals);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            var before = claims.ResolveClaim(kernel, item.Id)!;
            Assert.Equal(owner.Id.Value, before.OwnerGoalId);
            Assert.Equal(1, before.Version);
            Assert.Contains(item.Id, adapter.ReadBoard().LinkedBacklogItemIds);

            // Admission refuses a live link before invoking the mutation seam.
            using (var filing = new BoardFillFilingTestFixture())
            {
                filing.Drafts.Item = item;
                var draft = filing.Assessed();
                filing.Board = adapter.ReadBoard();
                Assert.Equal("backlog-linked", filing.Host.AttemptFiling(draft.Id).Reason);
                Assert.Empty(filing.Intake.Requests);
            }
            // Bypassing admission cannot steal a live slice owner's claim at commit.
            var refused = adapter.File(new("live-owner", path, item.Id, "full"), default);
            Assert.Equal("failed", refused.Kind);
            Assert.Contains("source-backlog-consumed", refused.Stderr);
            kernel = await repository.LoadAsync();
            Assert.Single(kernel.Goals);
            Assert.Equal(before, claims.ResolveClaim(kernel, item.Id));

            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            { Goals = snapshot.Goals.Select(goal => goal with { Status = status }).ToArray() });
            await repository.SaveAsync(kernel);
            Assert.DoesNotContain(item.Id, adapter.ReadBoard().LinkedBacklogItemIds);
            var filed = adapter.File(new("terminal-owner", path, item.Id, "full"), default);
            Assert.Equal("filed", filed.Kind);
            Assert.Equal(0, filed.ExitCode);
            kernel = await repository.LoadAsync();
            Assert.Equal(2, kernel.Goals.Count);
            var successor = kernel.Goals.Single(goal => goal.Id.Value == filed.GoalId);
            Assert.Equal(item.Id, successor.SourceBacklogItemId);
            Assert.Equal(SourceBacklogCoverage.Full, successor.SourceBacklogCoverage);
            var after = claims.ResolveClaim(kernel, item.Id)!;
            Assert.Equal(successor.Id.Value, after.OwnerGoalId);
            Assert.Equal(2, after.Version);
            Assert.Equal(SourceBacklogCoverage.Full, after.Coverage);
            Assert.Equal("replayed", adapter.File(new("terminal-owner", path, item.Id, "full"), default).Kind);
            Assert.Equal(after, claims.ResolveClaim(await repository.LoadAsync(), item.Id));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
