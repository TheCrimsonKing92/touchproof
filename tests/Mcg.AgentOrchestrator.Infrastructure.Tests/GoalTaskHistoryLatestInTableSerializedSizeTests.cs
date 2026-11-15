using System.Text;
using System.Text.Json.Nodes;

// Parallel-safe: both byte measurements use private databases and telemetry files.
public sealed class GoalTaskHistoryLatestInTableSerializedSizeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FourTasks_ReportInlineAndFlaggedBytesMeetingEightRecordNetBound()
    {
        using var flagged = new GoalTaskHistoryTestFixture();
        using var inline = new GoalTaskHistoryTestFixture();
        var goal = GoalTaskHistoryTestFixture.Snapshot(entries: 3, taskCount: 4);
        // The repository always strips eligible values. An equal-length one-field change
        // produces its inline baseline with identical history and unrelated content.
        var twin = goal with { Tasks = goal.Tasks.Select(task => task with
        {
            LastVerification = task.LastVerification! with { Command = "Verify" },
            LastDispatch = task.LastDispatch! with { Command = "Dispatch" }
        }).ToArray() };
        var payloadBytes = 0;
        for (var index = 0; index < 4; index++)
        {
            var task = goal.Tasks[index];
            Assert.Equal(Bytes(task.LastVerification), Bytes(twin.Tasks[index].LastVerification));
            Assert.Equal(Bytes(task.LastDispatch), Bytes(twin.Tasks[index].LastDispatch));
            payloadBytes += Bytes(task.LastVerification) + Bytes(task.LastDispatch);
        }
        await flagged.Repository.SaveGoalSnapshotsAsync([goal]);
        await inline.Repository.SaveGoalSnapshotsAsync([twin]);
        flagged.AssertLatestRepresentation(goal);
        inline.AssertLatestRepresentation(twin, false, false);
        Assert.Equal(flagged.HistoryRows(), inline.HistoryRows());
        var flaggedText = flagged.RawGoal(goal.Id);
        var inlineText = inline.RawGoal(goal.Id);
        var flaggedBytes = flagged.ReportedBytes();
        var inlineBytes = inline.ReportedBytes();
        Assert.Equal(Encoding.UTF8.GetByteCount(flaggedText), flaggedBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(inlineText), inlineBytes);
        var withoutFlags = JsonNode.Parse(flaggedText)!;
        foreach (var task in withoutFlags["Tasks"]!.AsArray())
        {
            Assert.True(task!["HistoryInTable"]!.AsObject().Remove("LatestVerificationInTable"));
            Assert.True(task["HistoryInTable"]!.AsObject().Remove("LatestDispatchInTable"));
        }
        var flagBytes = Encoding.UTF8.GetByteCount(flaggedText) - Encoding.UTF8.GetByteCount(withoutFlags.ToJsonString(GoalTaskHistoryTestFixture.Options));
        var netBound = payloadBytes - flagBytes;
        output.WriteLine($"four tasks: inlineBytes={inlineBytes}; flaggedBytes={flaggedBytes}; eightLatestBytes={payloadBytes}; flagBytes={flagBytes}; netBound={netBound}");
        Assert.True(netBound > 0);
        Assert.True(inlineBytes - flaggedBytes >= netBound);
        Assert.Equal(GoalTaskHistoryTestFixture.Json(goal), GoalTaskHistoryTestFixture.Json(
            await flagged.Repository.LoadGoalAsync(new(goal.Id))));
    }

    private static int Bytes<T>(T value) => Encoding.UTF8.GetByteCount(GoalTaskHistoryTestFixture.Json(value));
}
