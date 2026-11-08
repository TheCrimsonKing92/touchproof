using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the kernel, event files and injected observation times belong to this test.
public sealed class WorkerBuildEvidenceHoldOwnerTests
{
    [Fact]
    public void CompletionBuildHold_OperatorOwnsRemedy_StillEscalates()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var now = DateTimeOffset.UnixEpoch;
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Retained candidate edits", AgentRole.Developer);
            var goal = kernel.CreateGoal("Missing completion build evidence", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", root, now));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", root, 1, "", "build evidence unavailable", now,
                    OrchestratorFailureReason: "completion-build-coordinator-unavailable"));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Build evidence required");
            kernel.EscalateTaskFailure(goal.Id, task.Id, "Completion build coordinator unavailable");
            Assert.Equal(GoalStatus.Failed, goal.Status);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(WorkerBuildEvidenceRecoveryHold.Decide(
                goal, GoalLifecycleState.Failed, new FailedGoalRecoveryIdentity(goal.Id, task.Id, null, "fixture")));

            Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
            Assert.Equal(held.Reason, held.Decision!.Reason);
            Assert.Contains("scripts/Invoke-WorkerBuildCheck.ps1 on the goal worktree", held.Reason);
            Assert.Contains($"then closes task {task.Id.Value} with operator evidence", held.Reason);
            Assert.Contains(ConductorDriver.BuildAdjudicateRemedy(GoalStatus.Failed), held.Reason);
            Assert.Contains("adjudicate --goal <goal> <task#> close", held.Reason);

            var driver = ConductorDriverTests.MakeDriver();
            var timeline = new GoalLifecycleEventWriter(Path.Combine(root, "timeline"));
            driver.HoldEscalationEventWriter = timeline;
            var conduct = new ConductEventLogWriter(Path.Combine(root, "conduct.log"), utcNow: () => now);
            var changed = new HashSet<GoalId>();
            var lines = new List<string>();
            ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held, now,
                TimeSpan.FromMinutes(10), changed, lines, conduct);
            Assert.Equal(held.Reason, goal.CurrentHold!.Blocker);
            Assert.Null(goal.CurrentHold.StalledAt);
            foreach (var minutes in new[] { 11, 22 })
                ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal, held, now.AddMinutes(minutes),
                    TimeSpan.FromMinutes(10), changed, lines, conduct);

            Assert.NotNull(goal.CurrentHold.StalledAt);
            Assert.Single(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
            var escalation = ReadEscalation(timeline.EventFilePath(goal.Id), "eventType", "GoalEscalated", "reason");
            Assert.StartsWith("ownerless-hold-stalled ", escalation);
            Assert.EndsWith("blocker=" + held.Reason, escalation);
            Assert.Equal(escalation, ReadEscalation(conduct.CurrentPath, "eventKind", "goal-escalation", "detail"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadEscalation(string path, string key, string value, string textKey)
    {
        var entries = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty(key).GetString() == value)
                entries.Add(document.RootElement.GetProperty(textKey).GetString()!);
        }
        return Assert.Single(entries);
    }
}
