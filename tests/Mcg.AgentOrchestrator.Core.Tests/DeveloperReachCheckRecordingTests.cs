using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each round has its own in-memory kernel and fake clock.
public sealed class DeveloperReachCheckRecordingTests
{
    private const string Entry = """{"symbol":"ReadLabel","reference":"src/Label.cs:8","disposition":"updated"}""";
    private const string OtherEntry = """{"symbol":"ReadLabel","reference":"tests/LabelTests.cs:30","disposition":"covered-by LabelTests.Read"}""";
    private const string Json = "[" + Entry + "," + OtherEntry + "]";
    private const string NotePrefix = "DEVELOPER_REACH_CHECK ";

    [Fact]
    public void PresentReportRecordsOrderedEntriesOnDispatchAndRestoresThem()
    {
        var (goal, task) = Record("reach_check: " + Json, earlierReport: "reach_check: []");
        DeveloperReachCheckEntry[] expected =
        [
            new("ReadLabel", "src/Label.cs:8", "updated"),
            new("ReadLabel", "tests/LabelTests.cs:30", "covered-by LabelTests.Read")
        ];

        Assert.Equal(expected, Assert.Single(task.DispatchHistory).ReachCheck!.ToArray());
        Assert.Equal(expected, task.LastDispatch!.ReachCheck!.ToArray());
        Assert.Equal($"{NotePrefix}task={task.Id.Value} field=present entries=2", ReachNote(goal));
        var restored = RoundTrip(goal).FindTask(task.Id);
        Assert.Equal(expected, Assert.Single(restored.DispatchHistory).ReachCheck!.ToArray());
        Assert.Equal(expected, restored.LastDispatch!.ReachCheck!.ToArray());
    }

    [Fact]
    public void EmptyArraySurvivesSnapshotAsAnEmptyReport()
    {
        var (goal, task) = Record("reach_check: []");
        var restored = RoundTrip(goal).FindTask(task.Id);
        Assert.Empty(Assert.Single(restored.DispatchHistory).ReachCheck!);
        Assert.Empty(restored.LastDispatch!.ReachCheck!);
    }

    [Fact]
    public void OversizedArraysDiagnoseTheLimitAndStillComplete()
    {
        AssertRejected("reach_check: [" + string.Join(",", Enumerable.Repeat(Entry, 101)) + "]",
            "JSON array exceeds 100 entries.");
        var multibyte = Entry.Replace("src/Label.cs:8", new string('é', 17_000), StringComparison.Ordinal);
        AssertRejected("reach_check: [" + multibyte + "]", "JSON value exceeds 32768 UTF-8 bytes.");
    }

    [Theory]
    [InlineData("reach_check: [", "valid JSON on one line")]
    [InlineData("reach_check: [\n]", "valid JSON on one line")]
    [InlineData("reach_check:", "valid JSON on one line")]
    [InlineData("reach_check: null", "Expected a JSON array")]
    [InlineData("reach_check: {}", "Expected a JSON array")]
    [InlineData("reach_check: [1]", "exactly symbol, reference, and disposition")]
    [InlineData("""reach_check: [{"symbol":"x","reference":"y","disposition":"updated","extra":"x"}]""", "exactly symbol, reference, and disposition")]
    [InlineData("""reach_check: [{"symbol":"x","symbol":"y","disposition":"updated"}]""", "exactly symbol, reference, and disposition")]
    [InlineData("""reach_check: [{"symbol":"x","reference":8,"disposition":"updated"}]""", "exactly symbol, reference, and disposition")]
    [InlineData("""reach_check: [{"symbol":"x","reference":"y","disposition":" "}]""", "exactly symbol, reference, and disposition")]
    [InlineData("""reach_check: [{"symbol":"x","reference":"y","disposition":"covered-by "}]""", "Invalid reference disposition")]
    [InlineData("""reach_check: [{"symbol":"x","reference":"y","disposition":"Updated"}]""", "Invalid reference disposition")]
    [InlineData("reach_check: []\n**reach_check:** []", "Expected at most one reach_check line")]
    public void MalformedArrayIsDiagnosedAndStillCompletes(string field, string reason) =>
        AssertRejected(field, reason);

    [Fact]
    public void MissingFieldCompletesNormallyWithoutRecordingAReport()
    {
        var (absentGoal, absentTask) = Record(null, earlierReport: "reach_check: []");
        var (presentGoal, presentTask) = Record("reach_check: " + Json);

        Assert.Null(Assert.Single(absentTask.DispatchHistory).ReachCheck);
        Assert.Null(absentTask.LastDispatch!.ReachCheck);
        Assert.NotNull(presentTask.LastDispatch!.ReachCheck);
        Assert.DoesNotContain(absentGoal.Timeline, e => e.Message.StartsWith(NotePrefix, StringComparison.Ordinal));
        Assert.Equal(absentGoal.Timeline.Select(e => e.Kind), presentGoal.Timeline
            .Where(e => !e.Message.StartsWith(NotePrefix, StringComparison.Ordinal)).Select(e => e.Kind));
        Assert.Equal(presentTask.LastVerification!.CompletionVerdictRule, absentTask.LastVerification!.CompletionVerdictRule);

        var serialized = JsonSerializer.Serialize(absentGoal.ToSnapshot());
        Assert.DoesNotContain("\"ReachCheck\":", serialized, StringComparison.Ordinal);
        var restored = Goal.FromSnapshot(JsonSerializer.Deserialize<GoalSnapshot>(serialized)!);
        Assert.Null(restored.FindTask(absentTask.Id).LastDispatch!.ReachCheck);
    }

    private static void AssertRejected(string field, string reason)
    {
        var (goal, task) = Record(field);
        Assert.Null(Assert.Single(task.DispatchHistory).ReachCheck);
        Assert.Null(task.LastDispatch!.ReachCheck);
        var note = ReachNote(goal);
        Assert.StartsWith($"{NotePrefix}task={task.Id.Value} field=malformed reason=", note, StringComparison.Ordinal);
        Assert.Contains(reason, note, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', note);
    }

    private static string ReachNote(Goal goal) =>
        Assert.Single(goal.Timeline, e => e.Kind == ProgressKind.TaskNote &&
            e.Message.StartsWith(NotePrefix, StringComparison.Ordinal)).Message;

    private static Goal RoundTrip(Goal goal) =>
        Goal.FromSnapshot(JsonSerializer.Deserialize<GoalSnapshot>(JsonSerializer.Serialize(goal.ToSnapshot()))!);

    private static (Goal Goal, TaskSpec Task) Record(string? field, string? earlierReport = null)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Record Developer change reach.");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
        const string command = "codex exec prompt";
        const string directory = "C:\\repo";
        var output = (earlierReport is null ? string.Empty : $"WORKER_RESULT:\n{earlierReport}\nEND_WORKER_RESULT\n") +
            "WORKER_RESULT:\nfiles: src/Label.cs\ncommands: none\ntests: deferred - DeveloperReachCheckRecordingTests\n" +
            "commit: none\nblockers: none\nassigned_scope_complete: true\n" +
            (field is null ? string.Empty : field + "\n") +
            "model_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", command, directory, clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, "ce5e35c1");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            command, directory, 0, output, string.Empty, clock.UtcNow,
            WorkerResultPresent: true, HeartbeatStandardOutputBytes: output.Length));

        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.True(task.LastVerification!.CompletionVerdictVerifiedSuccess);
        return (goal, task);
    }
}
