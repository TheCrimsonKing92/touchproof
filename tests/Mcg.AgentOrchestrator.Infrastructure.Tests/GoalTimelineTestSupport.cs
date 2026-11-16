using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GoalTimelineTestSupport : IDisposable
{
    internal GoalTaskHistoryTestFixture Fixture { get; } = new();
    internal SqliteOrchestratorStateRepository Repository => Fixture.Repository;
    internal static JsonSerializerOptions Options => GoalTaskHistoryTestFixture.Options;
    internal static DateTimeOffset At => GoalTaskHistoryTestFixture.At;
    internal static string Json<T>(T value) => GoalTaskHistoryTestFixture.Json(value);

    // Legacy-format controls deliberately exercise inline JSON corruption and reference SQL.
    internal static void InlineStoredTimelines(string databasePath)
    {
        using var connection = StateDbConnectionFactory.Open(databasePath, StateDbConnectionProfile.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE goals SET snapshot_json = json_remove(json_set(snapshot_json, '$.Timeline',
                json(COALESCE((SELECT json_group_array(json(entry_json)) FROM
                    (SELECT entry_json FROM goal_timeline WHERE goal_id = goals.id ORDER BY sequence)), '[]'))),
                '$.TimelineInTable') WHERE json_type(snapshot_json, '$.TimelineInTable') = 'integer'
            """;
        command.ExecuteNonQuery();
    }

    internal static GoalSnapshot Snapshot()
    {
        var goal = GoalTaskHistoryTestFixture.LatestSnapshot();
        var payload = new ProgressEventSnapshot(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote,
            "All optional payloads café", At,
            RequeueSkipped: new(goal.Tasks[0].Id, goal.Id, "dispatch", "task", "Completed", "reason", "detail"),
            OperatorGates: [new("deliverable", "record", At, At, "satisfied")],
            OperatorIntentApplied: new("intent", "retry", goal.Tasks[0].Id, "operator", "cli", "local"),
            HumanInputSuperseded: new(["resolved"]),
            TickOutcome: new("continue", "active", null, new("test", "continue", 1, "evidence", "reason", [new("fact", "value")])),
            Failure: new("fixture", "rule", [new("fact", "value", "next")]),
            OperatorIntentCreatedAt: At.AddMinutes(-1),
            PreTesterEvidence: new("passed", "candidate", "receipt", ["test"], [], "result", []));
        var timeline = new[]
        {
            new ProgressEventSnapshot(goal.Id, null, ProgressKind.GoalCreated, "Created", At.AddDays(-1)),
            payload,
            new ProgressEventSnapshot(goal.Id, null, ProgressKind.TaskNote, "Same time, later sequence", At)
        };
        var seed = goal with { Timeline = timeline };
        var roundTrip = AgentOrchestratorKernel.FromSnapshot(new([seed], [])).ExportGoalSnapshot(new(seed.Id));
        Assert.Equal(Json(timeline), Json(roundTrip.Timeline));
        return roundTrip;
    }

    internal Task SeedAsync(GoalSnapshot goal, bool marked = true)
    {
        Fixture.InsertLegacy(goal);
        using var connection = Fixture.Open();
        GoalTimelineStore.CreateSchema(connection);
        for (var sequence = 0; sequence < goal.Timeline.Count; sequence++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO goal_timeline(goal_id, sequence, entry_json) VALUES ($id, $sequence, $entry)";
            command.Parameters.AddWithValue("$id", goal.Id);
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$entry", Json(goal.Timeline[sequence]));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        if (marked)
        {
            var document = JsonNode.Parse(Fixture.RawGoal(goal.Id))!.AsObject();
            document["TimelineInTable"] = goal.Timeline.Count;
            document.Remove("Timeline");
            Fixture.ReplaceDocument(goal.Id, document.ToJsonString(Options));
        }
        return Task.CompletedTask;
    }

    internal async Task AssertMarkedAsync(GoalSnapshot goal)
    {
        var document = JsonNode.Parse(Fixture.RawGoal(goal.Id))!.AsObject();
        Assert.False(document.ContainsKey("Timeline"));
        Assert.Equal(goal.Timeline.Count, document["TimelineInTable"]!.GetValue<int>());
        Assert.Equal(goal.Timeline.Select((entry, sequence) => $"{goal.Id}|{sequence}|{Json(entry)}"), Rows());
        Assert.Equal(Json(goal), Json(await Repository.LoadGoalAsync(new(goal.Id))));
    }

    internal static IEnumerable<string> Paths(bool archived = false)
    {
        // Full/board deliberately return metadata-only archived stubs; selected loads hydrate.
        if (!archived) { yield return "full"; yield return "board"; yield return "task-prefix"; }
        foreach (var path in new[] { "selected", "status", "goal", "goal-prefix", "cas", "state-cas", "merge", "epic", "similarity", "archive" })
            yield return path;
    }

    internal async Task<GoalSnapshot> ReadAsync(string path, GoalSnapshot expected)
    {
        var id = new GoalId(expected.Id);
        switch (path)
        {
            case "full": return (await Repository.LoadAsync()).ExportGoalSnapshot(id);
            case "board": return (await Repository.LoadBoardSnapshotAsync()).ExportGoalSnapshot(id);
            case "selected": return (await Repository.LoadGoalsAsync([id])).ExportGoalSnapshot(id);
            case "status": return (await Repository.LoadStatusGoalsAsync([id])).ExportGoalSnapshot(id);
            case "goal": return (await Repository.LoadGoalAsync(id))!;
            case "goal-prefix": return Assert.Single(await Repository.FindGoalSnapshotsByIdPrefixAsync(expected.Id[..8], 1));
            case "task-prefix": return Assert.Single(await Repository.FindTaskSnapshotsByIdPrefixAsync(expected.Tasks[0].Id, 1)).Goal;
            case "cas": return (await Repository.TransactGoalAsync(id, (goal, _) => Task.FromResult((false, goal, goal))))!;
            case "state-cas": return (await Repository.TransactGoalStateAsync(id, (state, _) => Task.FromResult((false, state, state!.Goal))))!;
            case "merge":
                var baseline = expected with { Objective = "Deliberately stale baseline" };
                var result = Assert.Single(await Repository.SaveGoalSnapshotsWithMergeAsync([new(baseline, baseline, RejectConflict: true)]));
                Assert.Equal(GoalSnapshotSaveDisposition.Skipped, result.Disposition);
                return result.PersistedSnapshot!;
            case "epic":
                using (var connection = Fixture.Open())
                {
                    var goal = Assert.Single(await SqliteOrchestratorStateRepository.ReadEpicGoalsAsync(connection, [expected.Id]));
                    return expected with
                    { Timeline = goal.Timeline.Select(evt => new ProgressEventSnapshot(evt.GoalId.Value, evt.TaskId?.Value,
                        evt.Kind, evt.Message, evt.OccurredAt, evt.RequeueSkipped, evt.OperatorGates, evt.OperatorIntentApplied,
                        evt.HumanInputSuperseded, evt.TickOutcome, evt.Failure, evt.OperatorIntentCreatedAt, evt.PreTesterEvidence)).ToArray() };
                }
            case "similarity":
                Assert.Equal(expected.Timeline.Max(evt => evt.OccurredAt),
                    Assert.Single(await Repository.ListCompletedGoalSimilarityDocumentsAsync()).UpdatedAt);
                return (await Repository.LoadGoalAsync(id))!;
            case "archive":
                using (var connection = Fixture.Open())
                {
                    var text = Fixture.RawGoal(expected.Id);
                    var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(text, Options)!;
                    snapshot = (await new GoalSnapshotArchive(Options).HydrateAsync(connection, snapshot))!;
                    return (await new GoalTimelineStore(Options).HydrateAsync(connection, snapshot, text))!;
                }
            default: throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    internal string[] Rows() => Fixture.Rows("SELECT goal_id || '|' || sequence || '|' || entry_json FROM goal_timeline ORDER BY goal_id, sequence");
    public void Dispose() => Fixture.Dispose();
}
