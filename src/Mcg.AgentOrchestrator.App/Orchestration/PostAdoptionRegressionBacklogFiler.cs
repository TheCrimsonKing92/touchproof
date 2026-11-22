using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using IncidentEvent = Mcg.AgentOrchestrator.App.Orchestration.PostAdoptionRegressionReverter.IncidentEvent;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PostAdoptionRegressionBacklogFiler
{
    internal static BacklogItem File(string orchestratorDirectory, IncidentEvent incident, string revertCommit,
        RevertPushPolicyGate.Result push, Func<string, string, BacklogItem>? add = null)
    {
        var title = $"Re-land reverted landing {incident.Landing}";
        var pushReason = push.Reason is null ? "" : $" ({push.Reason})";
        var body = new StringBuilder()
            .AppendLine($"Landing commit: {incident.Landing}")
            .AppendLine($"Revert commit: {revertCommit}")
            .AppendLine($"Trigger: {incident.Trigger}")
            .AppendLine($"Revert push: {push.Status}{pushReason}")
            .AppendLine($"Gate starved: {incident.GateStarved}")
            .AppendLine($"Gate slot free: {incident.GateSlotFree}")
            .AppendLine($"Verified goals: {incident.VerifiedGoalCount}");

        foreach (var goal in incident.AffectedGoals)
            body.AppendLine($"- {goal.GoalId}: before={FormatDecision(goal.Before)} after={FormatDecision(goal.After)}");

        var detail = body.AppendLine().Append("Re-land the change once the cause of the regression is fixed.").ToString();
        return add is null
            ? new BacklogStore(Path.Combine(orchestratorDirectory, "backlog.db"))
                .AddAsync(title, detail, sourceGoalId: null).GetAwaiter().GetResult()
            : add(title, detail);
    }

    private static string FormatDecision(PolicyDecisionRecord? decision) =>
        decision is null ? "none" : $"{decision.Stage}/{decision.Action} reason={decision.Reason}";
}
