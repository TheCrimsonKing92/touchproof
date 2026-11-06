using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class PlannerBriefAmendmentHoldAttentionView
{
    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload,
        IReadOnlyList<PlannerBriefAmendmentRevisionPendingHold>? holds)
    {
        if (holds is { Count: > 0 }) payload.Add("amendmentRevisionPending", holds.Select(hold => new
        {
            state = PlannerBriefAmendmentRevisionPendingHold.State, hold,
            detail = hold.Render(), identity = hold.Identity
        }).ToArray());
        return payload;
    }

    internal static PlannerBriefAmendmentRevisionPendingHold[] Holds(AgentOrchestratorKernel kernel, IEnumerable<Goal> goals) =>
        goals.Select(goal => kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id))
            .OfType<PlannerBriefAmendmentRevisionPendingHold>().ToArray();

    internal static bool Print(AgentOrchestratorKernel kernel, IEnumerable<Goal> goals, TextWriter? output = null)
    {
        var holds = Holds(kernel, goals);
        foreach (var hold in holds) (output ?? Console.Out).WriteLine($"  [{PlannerBriefAmendmentRevisionPendingHold.State}] {hold.Render()}");
        return holds.Length > 0;
    }
}
