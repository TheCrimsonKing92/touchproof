using Mcg.AgentOrchestrator.Core;

internal static class GoalTaskBuildHistoryTestSupport
{
    internal static GoalSnapshot Snapshot(int goalNumber = 1, int tasks = 4, int? openSequence = 9)
    {
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 0, taskCount: tasks);
        var id = goalNumber.ToString("x32");
        var at = GoalTaskHistoryTestFixture.At;
        var process = new WorkerBuildProcess(123, at, "fixture.exe");
        return seed with
        {
            Id = id,
            Timeline = seed.Timeline.Select(entry => entry with { GoalId = id }).ToArray(),
            Tasks = seed.Tasks.Select(task => task with
            {
                WorkerBuildInvocations = Enumerable.Range(0, 10).Select(sequence =>
                {
                    var startedAt = at.AddMinutes(sequence);
                    var open = sequence == openSequence;
                    return new WorkerBuildInvocation(
                        $"{task.Id}-{sequence}", $"request-{sequence}", id, task.Id, startedAt,
                        $"dispatch-{sequence}", "worktree", $"tree-{sequence}", "scope",
                        ["App.csproj"], "Debug", startedAt.AddHours(1), process, process,
                        "admission.db", 1, 1, open
                            ? [new(WorkerBuildPhase.Requested, startedAt)]
                            : [new(WorkerBuildPhase.Requested, startedAt), new(WorkerBuildPhase.Completed, startedAt)],
                        Outcome: open ? null : WorkerBuildOutcome.Cancelled,
                        CustodyReleased: !open,
                        Diagnostic: $"Fixture for task {task.Id}, sequence {sequence}.");
                }).ToArray()
            }).ToArray()
        };
    }
}
