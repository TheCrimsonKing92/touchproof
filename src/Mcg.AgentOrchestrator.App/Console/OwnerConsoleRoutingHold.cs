using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleRoutingHold
{
    internal static string? Describe(Goal goal, DateTimeOffset now)
    {
        if (goal.IsTerminal || goal.Status is GoalStatus.Parked or GoalStatus.WaitingForHuman) return null;
        var task = goal.Tasks.FirstOrDefault(task => OperatorRoutingNoteLedger.IsHeldRoundPending(task) &&
            now < task.AcceptanceRetryRoutingHold!.Deadline);
        if (task is null) return null;
        var hold = task.AcceptanceRetryRoutingHold!;
        if (hold.HoldSeconds == 0) return null;
        return $"waiting for operator routing: {Math.Max(0, (long)Math.Ceiling((hold.Deadline - now).TotalSeconds))}s left " +
            $"(deadline {hold.Deadline:O})";
    }
}
