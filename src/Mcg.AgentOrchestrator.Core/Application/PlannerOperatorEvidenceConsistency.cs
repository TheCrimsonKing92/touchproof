namespace Mcg.AgentOrchestrator.Core;

// Observational reconciliation: refinement/mapping already creates the canonical
// obligation. Keep it intact, including phase, scope, binding and all receipts.
public static class PlannerOperatorEvidenceConsistency
{
    public static PlannerOperatorEvidenceConsistencyHold? GetPlannerOperatorEvidenceConsistencyHold(
        this AgentOrchestratorKernel kernel, GoalId goalId)
    {
        lock (kernel.PlannerEvidenceRequestLock)
            return Evaluate(kernel.GetGoal(goalId), kernel.HumanInputRequests);
    }

    public static void EnsurePlannerOperatorEvidenceReady(this AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId)
    {
        var task = kernel.GetTask(goalId, taskId);
        // The Planner must remain executable to amend the request/brief.
        if (task.RequiredRole != AgentRole.Planner && kernel.GetPlannerOperatorEvidenceConsistencyHold(goalId) is { } hold)
            throw new InvalidOperationException(hold.Render());
    }

    internal static PlannerOperatorEvidenceConsistencyHold? Evaluate(
        Goal goal, IEnumerable<HumanInputRequest> requests)
    {
        if (goal.LifecycleStatus is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded ||
            goal.AuthoritativeRefinedSpecVersion is not { } version) return null;
        var recorded = requests.Where(item => item.GoalId == goal.Id).ToArray();
        var discrepancies = new List<PlannerOperatorEvidenceDiscrepancy>();
        foreach (var task in goal.Tasks.Where(item => item.RequiredRole == AgentRole.Planner))
        {
            if (!PlannerEvidenceCompletion.TryGetCurrentAcceptedBatch(goal, task, out var batch, out var brief, out var acceptedAt) ||
                batch is not { IsMalformed: false } || brief is null) continue;
            foreach (var directive in batch.Directives)
            {
                if (directive.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
                    directive.PlannerEvidence is not { Availability: "post-implementation" } payload ||
                    !string.Equals(payload.Owner, "operator", StringComparison.OrdinalIgnoreCase))
                    continue;
                var identity = new PlannerEvidenceIdentity(goal.Id.Value, task.Id.Value, brief.Value,
                    payload.CriterionIndex, payload.EvidenceKey);
                var index = payload.CriterionIndex - 1;
                var criterion = index >= 0 && index < version.Spec.AcceptanceCriteria.Count
                    ? version.Spec.AcceptanceCriteria[index].Trim() : "<outside authoritative criteria>";
                var matches = recorded.Where(item => item.TaskId == task.Id && item.PlannerIdentity == identity &&
                    item.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                    string.Equals(item.EvidenceOwner, "operator", StringComparison.OrdinalIgnoreCase) &&
                    item.SupersededByRequestId is null && item.BriefSupersession is null &&
                    payload.SemanticallyEquals(item.PlannerEvidence)).ToArray();
                if (matches.Length != 1 || index < 0 || index >= version.Spec.AcceptanceCriteria.Count ||
                    version.RecordedAt > (task.LastDispatch?.DispatchedAt ?? acceptedAt) ||
                    (task.LastDispatch is { } dispatch ? dispatch.PlannerEvidenceCriteriaVersionHash :
                        task.LastExecution?.PlannerEvidenceCriteriaVersionHash) != EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal) ||
                    (task.LastVerification?.AcceptanceCriteriaVersionHash is { Length: > 0 } acceptedCriteria &&
                     acceptedCriteria != EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal)) ||
                    string.IsNullOrWhiteSpace(payload.EvidenceKey) || string.IsNullOrWhiteSpace(payload.Needed) ||
                    string.IsNullOrWhiteSpace(payload.Reason) || payload.Store is not null || payload.StoreReference is not null ||
                    !string.Equals(directive.EvidenceOwner, "operator", StringComparison.OrdinalIgnoreCase))
                {
                    discrepancies.Add(new(identity, matches.FirstOrDefault()?.Id, version.Version, criterion,
                        PlannerOperatorEvidenceProblem.InvalidRequestProvenance,
                        "No unique schema-valid host-recorded request matching the current accepted Planner directive; replan."));
                    continue;
                }
                if (goal.DescribeOperatorEvidenceOwnershipProblem(index) is { } problem)
                    discrepancies.Add(new(identity, matches[0].Id, version.Version, criterion, problem.Problem, problem.Detail));
            }
        }
        return discrepancies.Count == 0 ? null : new(goal.Id, goal.AuthoritativeBrief.Version,
            discrepancies.OrderBy(item => item.Source.CriterionIndex).ThenBy(item => item.Source.StableId, StringComparer.Ordinal).ToArray());
    }
}
