using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: private kernels and strings; no shared state or I/O.
public sealed class TimelineMessageBoundTests
{
    private const string EventGoalId = "00112233445566778899aabbccddeeff";
    private const string EventTaskId = "ffeeddccbbaa99887766554433221100";
    private static readonly DateTimeOffset OccurredAt = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(16_001)]
    [InlineData(20_000)]
    [InlineData(2_100_000)]
    public void ExportGoalSnapshot_OversizedEvent_RetainsEndsWithoutMutatingLiveEvent(int length)
    {
        const string middle = "MIDDLE-SENTINEL";
        var message = "BEGIN-" + new string('a', length / 2 - 6) + middle
            + new string('z', length - length / 2 - middle.Length - 4) + "-END";
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Bound persisted timeline messages.");
        // Deliberately distinct from the containing goal: the marker must use the event's ids.
        var evt = new ProgressEvent(new GoalId(EventGoalId), new TaskId(EventTaskId),
            ProgressKind.TaskNote, message, OccurredAt);
        goal.Append(evt);

        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var stored = Assert.Single(snapshot.Timeline, item => item.Kind == ProgressKind.TaskNote);

        Assert.Equal(message, stored.Message);
        var bounded = TimelineMessageBound.Bound(stored.Message, stored.GoalId, stored.TaskId);
        AssertBound(message, bounded, EventTaskId);
        Assert.DoesNotContain(middle, bounded);
        Assert.StartsWith("BEGIN-", bounded);
        Assert.EndsWith("-END", bounded);
        Assert.Equal(EventGoalId, stored.GoalId);
        Assert.Equal(EventTaskId, stored.TaskId);
        Assert.Equal(OccurredAt, stored.OccurredAt);
        Assert.Equal(goal.Timeline.Count, snapshot.Timeline.Count);
        Assert.Same(evt, goal.Timeline.Last());
        Assert.Same(message, goal.Timeline.Last().Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15_999)]
    [InlineData(16_000)]
    public void ExportSnapshot_WithinLimit_PreservesMessageExactly(int length)
    {
        var message = new string('x', length);
        if (length >= 10)
            message = "\0\r\n\uD83D\uDE00\t" + message[6..];
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Preserve short messages.");
        goal.Append(new ProgressEvent(goal.Id, null, ProgressKind.TaskNote, message, OccurredAt));

        var snapshot = Assert.Single(kernel.ExportSnapshot().Goals);
        var stored = Assert.Single(snapshot.Timeline, item => item.Kind == ProgressKind.TaskNote);

        Assert.Equal(message, stored.Message);
        Assert.Same(message, stored.Message);
    }

    [Fact]
    public void ExportGoalSnapshot_TasklessEvent_LabelsTaskUnavailable()
    {
        var message = new string('a', 20_000);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Bound taskless timeline messages.");
        goal.Append(new ProgressEvent(new GoalId(EventGoalId), null,
            ProgressKind.TaskNote, message, OccurredAt));

        var stored = Assert.Single(kernel.ExportGoalSnapshot(goal.Id).Timeline,
            item => item.Kind == ProgressKind.TaskNote);

        Assert.Equal(message, stored.Message);
        AssertBound(message, TimelineMessageBound.Bound(stored.Message, stored.GoalId, stored.TaskId), null);
        Assert.Null(stored.TaskId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Bound_SupplementaryCharacters_PreservesPairsAndCountsCodeUnits(int prefixLength)
    {
        var message = new string('x', prefixLength)
            + string.Concat(Enumerable.Repeat("\uD83D\uDE00", 12_000));

        var bounded = TimelineMessageBound.Bound(message, EventGoalId, EventTaskId);

        AssertBound(message, bounded, EventTaskId, maxEndDifference: 2);
        // Strict encoding throws on a lone surrogate at either cut boundary.
        Assert.NotEmpty(new UTF8Encoding(false, true).GetBytes(bounded));
    }

    [Theory]
    [InlineData(16_840)]
    [InlineData(25_840)]
    [InlineData(115_840)]
    public void Bound_CountDigitChanges_ReportsActualOmissionWithinBudget(int length)
    {
        var message = new string('a', length);

        var bounded = TimelineMessageBound.Bound(message, EventGoalId, EventTaskId);

        AssertBound(message, bounded, EventTaskId);
    }

    [Fact]
    public void Bound_RepeatedCalls_IsDeterministicAndIdempotent()
    {
        var message = new string('a', 20_000);
        var bounded = TimelineMessageBound.Bound(message, EventGoalId, EventTaskId);

        AssertBound(message, bounded, EventTaskId);
        Assert.Equal(bounded, TimelineMessageBound.Bound(message, EventGoalId, EventTaskId));
        Assert.Same(bounded, TimelineMessageBound.Bound(bounded, EventGoalId, EventTaskId));
    }

    [Fact]
    public void Bound_LegacyNullMessage_PreservesNull() =>
        Assert.Null(TimelineMessageBound.Bound(null, EventGoalId, null));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Bound_IdentifiersExceedBudget_StillCapsMessage(bool longGoal, bool longTask)
    {
        var goalId = longGoal ? new string('g', 20_000) : EventGoalId;
        var taskId = longTask ? new string('t', 20_000) : EventTaskId;
        var message = new string('a', 20_000);

        var bounded = TimelineMessageBound.Bound(message, goalId, taskId);

        Assert.InRange(bounded.Length, 1, 16_000);
        Assert.Contains("unavailable (id exceeds message budget)", bounded);
        Assert.StartsWith("a", bounded);
        Assert.EndsWith("a", bounded);
    }

    private static void AssertBound(string original, string bounded, string? taskId, int maxEndDifference = 1)
    {
        Assert.InRange(bounded.Length, 1, 16_000);
        var marker = Regex.Match(bounded, @"\n\[omitted (?<count>[0-9]+) characters; goal id: [^\n]*\]\n");
        Assert.True(marker.Success, "Expected a readable omission marker.");
        Assert.Contains($"goal id: {EventGoalId};", marker.Value);
        Assert.Contains($"task id: {taskId ?? "unavailable"};", marker.Value);
        Assert.Contains("see task dispatch log", marker.Value);
        var head = bounded[..marker.Index];
        var tail = bounded[(marker.Index + marker.Length)..];
        Assert.NotEmpty(head);
        Assert.NotEmpty(tail);
        Assert.StartsWith(head, original);
        Assert.EndsWith(tail, original);
        var omitted = int.Parse(marker.Groups["count"].Value, CultureInfo.InvariantCulture);
        Assert.Equal(original.Length, head.Length + omitted + tail.Length);
        Assert.InRange(Math.Abs(head.Length - tail.Length), 0, maxEndDifference);
    }
}
