using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceRetryRoutingHoldGate
{
    internal static ConductorAdvanceOutcome.Held? Check(Goal goal, DateTimeOffset now, GoalLifecycleState state)
    {
        if (goal.IsTerminal || goal.Status == GoalStatus.Parked) return null;
        foreach (var task in goal.Tasks.Where(OperatorRoutingNoteLedger.IsHeldRoundPending))
        {
            var hold = task.AcceptanceRetryRoutingHold!;
            if (now < hold.Deadline)
            {
                var reason = Describe(task, now);
                return new(state, reason, $"operator-routing-hold:{task.Id.Value}:{hold.Deadline:O}")
                {
                    Decision = new("acceptance-retry-operator-routing", "Hold", 1, "routing-window-open", reason,
                        [new("task", task.Id.Value), new("round_retry_at", $"{hold.RoundRetryAt:O}"),
                         new("failure_recorded_at", $"{hold.FailureRecordedAt:O}"),
                         new("deadline", $"{hold.Deadline:O}"), new("observed_at", $"{now:O}")])
                };
            }
        }
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned))
            OperatorRoutingNoteLedger.DeliverPending(task);
        return null;
    }

    internal static string Describe(TaskSpec task, DateTimeOffset now)
    {
        var hold = task.AcceptanceRetryRoutingHold!;
        var seconds = Math.Max(0, (long)Math.Ceiling((hold.Deadline - now).TotalSeconds));
        return $"waiting-for-operator-routing task={task.Id.Value} deadline={hold.Deadline:O} remaining_seconds={seconds}";
    }
}
