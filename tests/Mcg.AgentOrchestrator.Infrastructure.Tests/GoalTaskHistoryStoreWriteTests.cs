using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns a database and telemetry destination.
public sealed class GoalTaskHistoryStoreWriteTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("creation")]
    public async Task EveryGoalWriteMirrorsBothHistories(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        if (path == "snapshots") await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(new GoalId(goal.Id), (_, _) =>
                Task.FromResult((true, (GoalSnapshot?)goal, true)));
        else
        {
            var kernel = AgentOrchestratorKernel.FromSnapshot(new([goal], []));
            goal = kernel.ExportGoalSnapshot(new GoalId(goal.Id));
            if (path == "kernel") await fixture.Repository.SaveAsync(kernel);
            else await fixture.Repository.TransactGoalCreationWithOutboxAsync(
                new GoalCreationLoadScope([], [], []), (loaded, _) =>
                {
                    loaded.ReplaceGoalStateWithSnapshot(goal, []);
                    return Task.FromResult((true, true, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                });
        }
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(40, fixture.HistoryRows().Length);
    }

    [Fact]
    public async Task RollbackRestoresDocumentVersionAndHistories()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var before = fixture.HistoryRows();
        var document = fixture.RawGoal(goal.Id);
        var version = fixture.Rows("SELECT CAST(version AS TEXT) FROM goals");
        var reachedCommit = false;
        var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath)
        {
            BeforeGoalStateOutboxCommit = () =>
            {
                reachedCommit = true;
                // This hook runs on the actual repository transaction after writing rows.
                throw new InvalidOperationException("history rollback sentinel");
            }
        };
        var revised = goal with { Objective = "Rollback", Tasks = [goal.Tasks[0] with { VerificationHistory = [] }] };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TransactGoalStateWithOutboxAsync(
            "history-rollback", new GoalId(goal.Id), (state, _) => Task.FromResult(
                (true, (GoalStateSnapshot?)(state! with { Goal = revised }), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[]))));
        Assert.Equal("history rollback sentinel", error.Message);
        Assert.True(reachedCommit);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(version, fixture.Rows("SELECT CAST(version AS TEXT) FROM goals"));
        Assert.Equal(before, fixture.HistoryRows());
    }

    [Fact]
    public async Task RollbackOfNewGoalLeavesNeitherGoalNorHistoryRows()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath)
        {
            BeforeGoalStateOutboxCommit = () => throw new InvalidOperationException("new goal rollback sentinel")
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TransactGoalStateWithOutboxAsync(
            "history-insert-rollback", new GoalId(goal.Id), (_, _) => Task.FromResult(
                (true, (GoalStateSnapshot?)new GoalStateSnapshot(goal, []), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[]))));
        Assert.Equal("new goal rollback sentinel", error.Message);
        Assert.Empty(fixture.Rows("SELECT id FROM goals"));
        Assert.Empty(fixture.HistoryRows());
    }

    [Fact]
    public async Task ExistingStoreAndUnchangedLegacyDocumentAcquireMirrorOnSave()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        fixture.InsertLegacy(goal);
        fixture.Execute("DROP TABLE goal_task_history");
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal("1", SqliteOrchestratorStateRepository.ValidateReadOnlySchema(fixture.DatabasePath));
    }
}
