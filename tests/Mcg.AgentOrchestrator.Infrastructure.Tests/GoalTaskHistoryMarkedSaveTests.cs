using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every save and transaction uses a private fixture database.
public sealed class GoalTaskHistoryMarkedSaveTests
{
    [Theory]
    [InlineData("snapshots")]
    [InlineData("kernel")]
    [InlineData("cas")]
    [InlineData("state-cas")]
    public async Task MarkedLoad_EverySave_KeepsAllRows(string path)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        var goal = AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new(seed.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var before = fixture.HistoryRows();
        fixture.MarkStoredHistory(goal.Id);
        var id = new GoalId(goal.Id);
        if (path == "snapshots")
            await fixture.Repository.SaveGoalSnapshotsAsync([(await fixture.Repository.LoadGoalAsync(id))!]);
        else if (path == "kernel")
            await fixture.Repository.SaveAsync(await fixture.Repository.LoadGoalsAsync([id]));
        else if (path == "cas")
            await fixture.Repository.TransactGoalAsync(id, (loaded, _) =>
                Task.FromResult((true, loaded, true)));
        else
            await fixture.Repository.TransactGoalStateWithOutboxAsync("marked-save", id, (loaded, _) =>
                Task.FromResult((true, loaded, true, (IReadOnlyList<OrchestratorStateOutboxMessage>)[])));
        Assert.Equal(40, before.Length);
        Assert.Equal(before, fixture.HistoryRows());
        await fixture.AssertMirroredAsync(goal);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(id)));
    }

    [Fact]
    public async Task MarkedLoad_KernelAppend_PreservesTenOldEntriesAndAddsOne()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.Snapshot();
        var goal = AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new(seed.Id));
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var before = fixture.HistoryRows();
        fixture.MarkStoredHistory(goal.Id);
        var kernel = await fixture.Repository.LoadGoalsAsync([new(goal.Id)]);
        kernel.RecordTaskVerification(new(goal.Id), new(goal.Tasks[0].Id), new TaskVerificationRecord(
            "new verification round", "fixture", 0, "appended", "", GoalTaskHistoryTestFixture.At.AddDays(1),
            DispatchStartedAt: GoalTaskHistoryTestFixture.At.AddHours(1)));
        var expected = kernel.ExportGoalSnapshot(new(goal.Id));
        Assert.Equal(11, expected.Tasks[0].VerificationHistory!.Count);
        await fixture.Repository.SaveAsync(kernel);
        var after = fixture.HistoryRows();
        Assert.Equal(41, after.Length);
        Assert.All(before, row => Assert.Contains(row, after));
        var added = Assert.Single(after.Except(before));
        Assert.Contains($"|{goal.Tasks[0].Id}|verification|10|", added);
        Assert.Contains("appended", added);
        await fixture.AssertMirroredAsync(expected);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(expected), GoalTaskHistoryTestFixture.Json(
            await fixture.Repository.LoadGoalAsync(new(goal.Id))));
    }

    [Fact]
    public async Task UnmarkedSave_ConvertsToMarkedDocumentWithoutChangingSnapshot()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot();
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        var expected = JsonSerializer.SerializeToUtf8Bytes(goal, options);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(
            await fixture.Repository.LoadGoalAsync(new(goal.Id)), options));
        await fixture.AssertMirroredAsync(goal);
        Assert.Contains("\"HistoryInTable\"", fixture.RawGoal(goal.Id));
    }
}
