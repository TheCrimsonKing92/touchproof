namespace Mcg.AgentOrchestrator.Core;

// Classifies durable request/answer authority. The kernel owns the resulting mutation.
internal static class PlannerBriefAmendmentRouting
{
    internal static PlannerBriefAmendmentRevisionPendingHold? GetPendingHold(Goal goal, IEnumerable<HumanInputRequest> requests)
    {
        if (goal.IsTerminal || goal.Status == GoalStatus.Parked) return null;
        foreach (var task in goal.Tasks)
            if (Evaluate(goal, task, requests) is { Replan: false } decision)
                return decision.Hold(goal, task);
        return null;
    }

    internal sealed record Decision(HumanInputRequest Request, bool Revised, bool Declined)
    {
        internal bool Replan => Revised || Declined;
        internal PlannerBriefAmendmentRevisionPendingHold Hold(Goal goal, TaskSpec task) =>
            new(goal.Id, task.Id, Request.Id, Request.Amendment!.CriterionIndex,
                Request.Amendment.RequestedBriefVersion, goal.AuthoritativeBrief.Version);
        internal string RetryMessage(Goal goal) =>
            $"Planner brief amendment criterion={Request.Amendment!.CriterionIndex} request={Request.Id.Value}: " +
            (Revised ? "authoritative brief revised" : "typed decline recorded") +
            $"; replan against current brief v{goal.AuthoritativeBrief.Version}." +
            $"{Environment.NewLine}Amendment answer: {Request.Answer}";
    }

    internal static Decision? Evaluate(Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests)
    {
        if (task.RequiredRole != AgentRole.Planner || task.Status != WorkTaskStatus.WaitingForHuman ||
            task.LastProcess is { IsRunning: true }) return null;
        var owned = requests.Where(request => request.GoalId == goal.Id && request.TaskId == task.Id).ToArray();
        if (owned.Any(request => !request.IsCompleted && request.IsCurrentForBrief(goal.AuthoritativeBrief.Version) &&
                HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind))) return null;
        var amendment = owned.Where(request => request.Kind == HumanWaitKind.PlannerBriefAmendment &&
                request.Amendment is not null && !request.AmendmentReplanRouted && request.AuthoritativeAnswer is not null &&
                (task.LatestRetryAt is null || request.RequestedAt >= task.LatestRetryAt))
            .OrderByDescending(request => request.RequestedAt).ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        return amendment is null ? null : new(amendment,
            goal.AuthoritativeBrief.Version > amendment.Amendment!.RequestedBriefVersion,
            amendment.AuthoritativeAnswer!.AmendmentDisposition == PlannerBriefAmendmentDisposition.Decline);
    }
}
