using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every case owns its database.
public sealed class GoalTaskHistoryLatestInTableReadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MarkedLatest_AllHydratingPaths_RestoreNewestRows(bool archived, bool staleInline)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        if (staleInline)
        {
            var document = JsonNode.Parse(fixture.RawGoal(goal.Id))!;
            document["Tasks"]![0]!["LastVerification"] = JsonSerializer.SerializeToNode(goal.Tasks[1].LastVerification, GoalTaskHistoryTestFixture.Options);
            document["Tasks"]![0]!["LastDispatch"] = JsonSerializer.SerializeToNode(goal.Tasks[1].LastDispatch, GoalTaskHistoryTestFixture.Options);
            fixture.ReplaceDocument(goal.Id, document.ToJsonString(GoalTaskHistoryTestFixture.Options));
        }
        fixture.MarkStoredLatest(goal.Id, keepInline: staleInline);
        if (archived)
            Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
        var before = fixture.RawGoal(goal.Id);
        foreach (var loaded in await fixture.ReadLatestPathsAsync(goal, archived)) AssertLatest(goal, loaded);
        Assert.Equal(before, fixture.RawGoal(goal.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Flags_IndependentlyOverrideOnlyMarkedLatest(bool verification, bool dispatch)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        var goal = seed with { Tasks = seed.Tasks.Select(task => task with
        {
            LastVerification = task.VerificationHistory![0], LastDispatch = task.DispatchHistory![0]
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id, verification, dispatch, keepInline: true);
        var expected = goal with { Tasks = goal.Tasks.Select(task => task with
        {
            LastVerification = verification ? task.VerificationHistory![^1] : task.LastVerification,
            LastDispatch = dispatch ? task.DispatchHistory![^1] : task.LastDispatch
        }).ToArray() };
        foreach (var loaded in await fixture.ReadLatestPathsAsync(goal)) AssertLatest(expected, loaded);
    }

    [Fact]
    public async Task AbsentAndFalseFlags_PreserveDifferingInlineRecordsAcrossPaths()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        var goal = seed with { Tasks = seed.Tasks.Select(task => task with
        {
            LastVerification = task.VerificationHistory![0], LastDispatch = task.DispatchHistory![0]
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        var absent = await fixture.ReadLatestPathsAsync(goal);
        fixture.MarkStoredLatest(goal.Id, false, false);
        var explicitFalse = await fixture.ReadLatestPathsAsync(goal);
        Assert.Equal(absent.Count, explicitFalse.Count);
        foreach (var loaded in absent.Concat(explicitFalse))
        {
            AssertLatest(goal, loaded);
            foreach (var task in loaded.Tasks)
            {
                var source = goal.Tasks.Single(t => t.Id == task.Id);
                Assert.NotEqual(GoalTaskHistoryTestFixture.Json(source.VerificationHistory![^1]), GoalTaskHistoryTestFixture.Json(task.LastVerification));
                Assert.NotEqual(GoalTaskHistoryTestFixture.Json(source.DispatchHistory![^1]), GoalTaskHistoryTestFixture.Json(task.LastDispatch));
            }
        }
    }

    [Fact]
    public async Task MarkedLatest_UsesHighestSequenceRatherThanNewestTimestamp()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var seed = GoalTaskHistoryTestFixture.LatestSnapshot();
        var goal = seed with { Tasks = seed.Tasks.Select(task =>
        {
            var verification = task.VerificationHistory!.ToArray();
            var dispatch = task.DispatchHistory!.ToArray();
            verification[^1] = verification[^1] with { CompletedAt = GoalTaskHistoryTestFixture.At.AddDays(-2) };
            dispatch[^1] = dispatch[^1] with { DispatchedAt = GoalTaskHistoryTestFixture.At.AddDays(-2) };
            return task with { VerificationHistory = verification, LastVerification = verification[^1],
                DispatchHistory = dispatch, LastDispatch = dispatch[^1] };
        }).ToArray() };
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
        fixture.MarkStoredLatest(goal.Id);
        AssertLatest(goal, (await fixture.Repository.LoadGoalAsync(new(goal.Id)))!);
    }

    private static void AssertLatest(GoalSnapshot expected, GoalSnapshot loaded)
    {
        Assert.NotEmpty(loaded.Tasks);
        foreach (var task in loaded.Tasks)
        {
            var source = expected.Tasks.Single(t => t.Id == task.Id);
            Assert.Equal(GoalTaskHistoryTestFixture.Json(source.LastVerification), GoalTaskHistoryTestFixture.Json(task.LastVerification));
            Assert.Equal(GoalTaskHistoryTestFixture.Json(source.LastDispatch), GoalTaskHistoryTestFixture.Json(task.LastDispatch));
        }
    }
}
