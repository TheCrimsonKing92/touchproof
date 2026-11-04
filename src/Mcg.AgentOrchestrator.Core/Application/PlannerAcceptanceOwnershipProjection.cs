namespace Mcg.AgentOrchestrator.Core;

// Owns projection and replay of accepted Planner execution ownership. The kernel
// supplies synchronization and task completion; this type never grants a source verdict.
internal sealed class PlannerAcceptanceOwnershipProjection(
    object requestLock,
    IClock clock,
    IEnumerable<HumanInputRequest> humanInputRequests,
    Action<Goal, TaskId?, ProgressKind, string> append)
{
    internal bool HasRecordedExecutionRequests(Goal goal) =>
        goal.GateTestObligations.Any(item => item.PlannerIdentity is not null) ||
        humanInputRequests.Any(item => item.GoalId == goal.Id && item.PlannerIdentity is not null &&
            GateTestObligation.IsAcceptanceGateOwner(item.EvidenceOwner));

    internal string? Project(Goal goal, TaskSpec task,
        HumanInputDirectiveParseResult batch, int? briefVersion, DateTimeOffset recordedAt)
    {
        lock (requestLock)
        {
            if (HasTerminalLifecycle(goal)) return null;
            var diagnostic = PlannerEvidenceRecorder.PlanOwnership(goal, task, batch, briefVersion, recordedAt, out var changes);
            if (diagnostic is not null) return diagnostic;
            ApplyPlannerOwnership(goal, changes);
            return null;
        }
    }

    private void ApplyPlannerOwnership(Goal goal, IReadOnlyList<CriterionEvidenceObligation> changes)
    {
        goal.ApplyPlannerAcceptanceOwnership(changes);
        foreach (var item in changes)
            append(goal, null, ProgressKind.GoalPolicyDecision,
                $"PLANNER_EXECUTION_OWNERSHIP obligation={item.DisplayLabel}; owner={item.Owner}; state={item.State}; " +
                $"scope={item.RequiredScope}; sources={string.Join(",", (item.PlannerSources ?? []).Select(source => source.Identity.StableId))}");
    }

    // Runs inside the existing goal lifecycle projection/save boundary. Plan every
    // sibling before mutation; persistence conflicts retain stored authority and
    // force the conductor to reload rather than publish an uncommitted exemption.
    internal bool Reconcile(Goal goal, Func<Goal, TaskSpec, bool> completeDeferredReview)
    {
        if (HasTerminalLifecycle(goal)) return false;
        lock (requestLock)
        {
            var sources = new List<PlannerCriterionEvidenceSource>();
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole == AgentRole.Planner && task.Status == WorkTaskStatus.Completed))
            {
                if (!PlannerEvidenceCompletion.TryGetCurrentAcceptedBatch(goal, task, out var batch, out var brief, out var at) ||
                    batch is null || batch.Directives.Count == 0) continue;
                var diagnostic = PlannerEvidenceRecorder.PlanOwnership(goal, task, batch, brief, at, out var planned);
                if (diagnostic is not null) throw new InvalidOperationException(diagnostic);
                // Require the original host-recorded identity/payload, never a task
                // status or a rehydrated worker claim alone.
                foreach (var source in planned.SelectMany(item => item.PlannerSources ?? []).Where(source =>
                    source.Identity.GoalId == goal.Id.Value && source.Identity.RequestingTaskId == task.Id.Value &&
                    source.Identity.BriefVersion == brief && batch.Directives.Any(directive =>
                        source.Directive.SemanticallyEquals(directive.PlannerEvidence))))
                {
                    var recorded = goal.GateTestObligations.Any(item => item.PlannerIdentity == source.Identity &&
                            source.Directive.SemanticallyEquals(item.PlannerEvidence)) ||
                        humanInputRequests.Any(item => item.GoalId == goal.Id && item.PlannerIdentity == source.Identity &&
                            source.Directive.SemanticallyEquals(item.PlannerEvidence));
                    if (!recorded) throw new InvalidOperationException($"Planner ownership has no matching recorded directive: {source.Identity.StableId}.");
                    sources.Add(source);
                }
            }
            var conflict = goal.PlanPlannerAcceptanceOwnership(sources, clock.UtcNow, out var changes);
            if (conflict is not null) throw new InvalidOperationException(conflict);
            ApplyPlannerOwnership(goal, changes);
            var reconciled = changes.Count > 0;
            foreach (var reviewer in goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer && task.Status == WorkTaskStatus.Failed))
            {
                if (!CanReconcileDeferredPlannerReview(goal, reviewer)) continue;
                reconciled |= completeDeferredReview(goal, reviewer);
            }
            return reconciled;
        }
    }

    private static bool HasTerminalLifecycle(Goal goal) =>
        goal.LifecycleStatus is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;

    private static bool CanReconcileDeferredPlannerReview(Goal goal, TaskSpec task)
    {
        if (task.LastVerification is not { Succeeded: true, WorkerResultPresent: true, ReviewFindingContractViolation: null } verification ||
            task.LastDispatch is not { BriefSnapshot: not null } dispatch || dispatch.BriefVersion != goal.AuthoritativeBrief.Version ||
            verification.DispatchAttemptAt != dispatch.DispatchedAt || string.IsNullOrWhiteSpace(verification.ReviewedCommit) ||
            verification.AcceptanceCriteriaVersionHash != EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal) ||
            !WorkerResultBlockers.AssessSubscriptionVerification(task, verification).AllowsCompletion ||
            !WorkerResultBlockers.TryFindPassVerdict(verification) ||
            WorkerResultBlockers.TryFindHardFailureBlocker(verification, out _) ||
            ReviewFindings.GetOpenBlockingFindings(verification.MergedReviewFindings ?? [], goal.EffectiveAcceptanceCriteriaCorrections).Count > 0 ||
            !WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out _) ||
            goal.RefinedSpec is not { } spec ||
            !verdicts.Select(item => item.CriterionIndex).OrderBy(index => index).SequenceEqual(Enumerable.Range(0, spec.AcceptanceCriteria.Count)) ||
            verdicts.Any(item => item.Verdict != "met" && (item.Verdict != "not-verifiable" ||
                VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(goal, item.CriterionIndex))) ||
            !verdicts.Any(item => item.Verdict == "not-verifiable" && goal.CriterionEvidenceObligations.Any(obligation =>
                obligation.CriterionVersion == goal.AuthoritativeRefinedSpecVersion?.Version && obligation.CriterionIndex == item.CriterionIndex &&
                obligation.PlannerSources is { Count: > 0 }))) return false;
        // Repair only the missing-ownership attestation outcome, never arbitrary
        // failed source review, cancellation, historical verdict or answer bytes.
        return goal.Timeline.LastOrDefault(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskFailed)?.Message
            .StartsWith("Reviewer WORKER_RESULT criteria attestation rejected:", StringComparison.Ordinal) == true;
    }
}
