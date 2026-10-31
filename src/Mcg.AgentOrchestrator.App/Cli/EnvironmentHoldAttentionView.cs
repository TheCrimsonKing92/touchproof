using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class EnvironmentHoldAttentionView
{
    private const string NextAction = "Restore the contracted tool; probes retry automatically. Restart the conductor after changing its inherited environment.";

    private static IEnumerable<Goal> Held(IEnumerable<Goal> goals) => goals
        .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
        .Where(goal => goal.CurrentHold?.State == EnvironmentHold.State);

    internal static object[] Rows(IEnumerable<Goal> goals) => Held(goals).Select(goal => (object)new
    {
        goalId = goal.Id.Value, state = EnvironmentHold.State, owner = "operator",
        hold = EnvironmentHold.TryRead(goal), detail = goal.CurrentHold!.Blocker, nextAction = NextAction
    }).ToArray();

    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload, IEnumerable<Goal> goals)
    {
        var rows = Rows(goals);
        if (rows.Length > 0) payload.Add("environmentHolds", rows);
        return payload;
    }

    internal static bool Print(IEnumerable<Goal> goals, TextWriter? output = null)
    {
        var held = Held(goals).ToArray();
        foreach (var goal in held)
        {
            var hold = EnvironmentHold.TryRead(goal);
            (output ?? Console.Out).WriteLine($"  [{EnvironmentHold.State}] goal={goal.Id.Value[..8]} owner=operator tool={hold?.Tool ?? "contract"} " +
                $"probe={hold?.Command} rule={hold?.Rule} exit={hold?.ExitCode?.ToString() ?? hold?.Outcome} output={hold?.OutputTail ?? goal.CurrentHold!.Blocker}");
            (output ?? Console.Out).WriteLine($"    next: {NextAction}");
        }
        return held.Length > 0;
    }
}
