using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database and telemetry destination.
public sealed class GoalTaskHistoryMarkedWriteTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    [InlineData("creation")]
    [InlineData("merge")]
    public async Task EveryWritePathStoresOnlyMarkedDocumentsAndMatchingRows(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([seed], []));
        var goal = kernel.ExportGoalSnapshot(new(seed.Id));
        var before = GoalTaskHistoryTestFixture.Json(goal);
        if (path == "snapshots") await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        else if (path == "kernel") await fixture.Repository.SaveAsync(kernel);
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(new(goal.Id), (_, _) =>
                Task.FromResult((true, (GoalSnapshot?)goal, true)));
        else if (path == "state-cas")
            await fixture.Repository.TransactGoalStateWithOutboxAsync("marked-insert", new(goal.Id), (_, _) =>
                Task.FromResult((true, (GoalStateSnapshot?)new GoalStateSnapshot(goal, []), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[])));
        else if (path == "creation")
            await fixture.Repository.TransactGoalCreationWithOutboxAsync(new GoalCreationLoadScope([], [], []), (loaded, _) =>
            {
                loaded.ReplaceGoalStateWithSnapshot(goal, []);
                return Task.FromResult((true, true, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
            });
        else
        {
            await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
            var current = goal with { Objective = "Merge write" };
            var result = Assert.Single(await fixture.Repository.SaveGoalSnapshotsWithMergeAsync(
                [new GoalSnapshotSaveRequest(goal, current)]));
            Assert.Equal(GoalSnapshotSaveDisposition.Saved, result.Disposition);
            goal = current;
            before = GoalTaskHistoryTestFixture.Json(current);
        }
        Assert.Equal(before, GoalTaskHistoryTestFixture.Json(goal));
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(40, fixture.HistoryRows().Length);
    }

    [Fact]
    public async Task MixedNullEmptyAndPopulatedListsHaveExactMarkerCounts()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(taskCount: 3);
        goal = goal with { Tasks = [goal.Tasks[0], goal.Tasks[1] with
            { VerificationHistory = [], DispatchHistory = null }, goal.Tasks[2] with
            { VerificationHistory = null, DispatchHistory = [] }] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(20, fixture.HistoryRows().Length);
    }

    [Fact]
    public async Task HistoryOnlyChangeAdvancesVersionAndTimestampButIdenticalSaveDoesNot()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.Execute("UPDATE goals SET updated_at = 'before-history-change'");
        var document = fixture.RawGoal(goal.Id);
        var version = int.Parse(Assert.Single(fixture.Rows("SELECT CAST(version AS TEXT) FROM goals")));
        var entries = goal.Tasks[0].VerificationHistory!.ToArray();
        entries[0] = entries[0] with { StandardOutput = "revised past entry" };
        goal = goal with { Tasks = [goal.Tasks[0] with { VerificationHistory = entries }, goal.Tasks[1]] };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(version + 1, int.Parse(Assert.Single(fixture.Rows("SELECT CAST(version AS TEXT) FROM goals"))));
        Assert.NotEqual("before-history-change", Assert.Single(fixture.Rows("SELECT updated_at FROM goals")));
        await fixture.AssertMirroredAsync(goal);
        var row = fixture.Rows("SELECT version || '|' || updated_at FROM goals");
        var history = fixture.HistoryRows();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        Assert.Equal(row, fixture.Rows("SELECT version || '|' || updated_at FROM goals"));
        Assert.Equal(history, fixture.HistoryRows());
        using var receipt = JsonDocument.Parse(File.ReadAllLines(fixture.DiagnosticsPath)
            .Last(line => line.Contains("sqlite-state-write-hold", StringComparison.Ordinal)));
        Assert.Equal(0, receipt.RootElement.GetProperty("serializedBytes").GetInt64());
    }

    [Fact]
    public async Task RejectedCommitRollsBackMarkedDocumentVersionAndRowsTogether()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var document = fixture.RawGoal(goal.Id);
        var history = fixture.HistoryRows();
        var version = fixture.Rows("SELECT version || '|' || updated_at FROM goals");
        var reachedCommit = false;
        var repository = new SqliteOrchestratorStateRepository(fixture.DatabasePath)
        {
            BeforeGoalStateOutboxCommit = () =>
            {
                reachedCommit = true;
                throw new InvalidOperationException("marked rollback sentinel");
            }
        };
        var revised = goal with { Tasks = [goal.Tasks[0] with { VerificationHistory = [] }] };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TransactGoalStateWithOutboxAsync(
            "marked-rollback", new(goal.Id), (state, _) => Task.FromResult(
                (true, (GoalStateSnapshot?)(state! with { Goal = revised }), true,
                    (IReadOnlyList<OrchestratorStateOutboxMessage>)[]))));
        Assert.Equal("marked rollback sentinel", error.Message);
        Assert.True(reachedCommit);
        Assert.Equal(document, fixture.RawGoal(goal.Id));
        Assert.Equal(history, fixture.HistoryRows());
        Assert.Equal(version, fixture.Rows("SELECT version || '|' || updated_at FROM goals"));
        await fixture.AssertMirroredAsync(goal);
    }
}
