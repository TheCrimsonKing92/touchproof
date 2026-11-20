namespace Mcg.AgentOrchestrator.Core;

// Owns validation and routing of a complete Planner evidence batch under the kernel request lock.
internal sealed class PlannerEvidenceRecorder(
    AgentOrchestratorKernel kernel,
    object requestLock,
    IClock clock,
    Action<Goal, TaskId?, ProgressKind, string> append,
    Func<Goal, TaskSpec, IReadOnlyList<DeclaredGateCheck>>? declaredGateCheckSource,
    Func<Goal, TaskSpec, IReadOnlyList<RunnerTestIdentity>?>? testIdentityListingSource)
{
    internal static bool HasCurrentBlockingHumanInput(Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests)
    {
        var pending = requests.Where(item => item.GoalId == goal.Id && item.TaskId == task.Id &&
            !item.IsCompleted && HumanWaitPolicyDefaults.BlocksActiveWork(item.Kind)).ToArray();
        return pending.Any(item => item.IsCurrentForBrief(goal.AuthoritativeBrief.Version)) ||
            task.Status == WorkTaskStatus.WaitingForHuman && !pending.Any(item => !item.IsCurrentForBrief(goal.AuthoritativeBrief.Version));
    }

    internal static bool IsObsoletePlannerEvidenceRound(Goal goal, TaskSpec task) =>
        task.RequiredRole == AgentRole.Planner && task.LastDispatch is { BriefSnapshot: not null } dispatch &&
        dispatch.BriefVersion != goal.AuthoritativeBrief.Version &&
        task.LastVerification is { DispatchAttemptAt: not null, PlannerEvidenceBatch: { } batch } &&
        batch.Directives.Any(item => item.PlannerEvidence is not null);

    internal static bool IsSupersededBriefPlannerRound(Goal goal, TaskSpec task) =>
        task.RequiredRole == AgentRole.Planner && task.LastDispatch is { BriefSnapshot: not null } dispatch &&
        dispatch.BriefVersion != goal.AuthoritativeBrief.Version && task.LastProcess is not { IsRunning: true };

    // Called by host execution paths only. Validation completes before any obligation is published.
    internal string? Record(Goal goal, TaskSpec task,
        HumanInputDirectiveParseResult batch, int? dispatchBriefVersion, out bool blocksActiveWork)
    {
        blocksActiveWork = false;
        if (batch.IsMalformed) return batch.Diagnostic;
        if (task.RequiredRole != AgentRole.Planner)
            return "Planner evidence requires a Planner task.";
        if (batch.EvidenceLineCount > 1 && dispatchBriefVersion is null)
            return "Plural Planner evidence requires authoritative dispatch brief provenance; replan with a new dispatch.";
        var listing = PlannerTestIdentityListing.Snapshot(batch.Directives
            .Where(directive => directive.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                GateTestObligation.IsAcceptanceGateOwner(directive.EvidenceOwner))
            .SelectMany(directive => directive.TestNames ?? []), goal, task, testIdentityListingSource);
        lock (requestLock)
        {
            if (dispatchBriefVersion is not null && dispatchBriefVersion != goal.AuthoritativeBrief.Version)
                return "Planner evidence belongs to an obsolete dispatch brief; replan against the current brief.";
            if (dispatchBriefVersion is not null && goal.RefinedSpec is null)
                return "Typed Planner evidence requires authoritative acceptance criteria for range validation; refine the brief and replan.";
            if (batch.Directives.Any(directive => directive.Kind == HumanWaitKind.PlannerBriefAmendment &&
                    directive.AmendmentCriterionIndex is not > 0))
                return "Planner brief amendment requires a positive criterion index.";
            var items = new List<(HumanInputDirective Directive, PlannerEvidenceIdentity? Identity)>();
            foreach (var directive in batch.Directives)
            {
                if (directive.Kind == HumanWaitKind.PlannerBriefAmendment) continue;
                if (directive.PlannerEvidence is not { } payload)
                    return "Planner evidence transport is missing a validated payload; replan.";
                if (goal.RefinedSpec is { } spec && payload.CriterionIndex > spec.AcceptanceCriteria.Count)
                    return $"Planner evidence criterion {payload.CriterionIndex} is outside the authoritative criteria; replan.";
                var identity = dispatchBriefVersion is { } version
                    ? new PlannerEvidenceIdentity(goal.Id.Value, task.Id.Value, version, payload.CriterionIndex, payload.EvidenceKey)
                    : null;
                if (identity is not null)
                {
                    var storedPayloads = kernel.HumanInputRequests.Where(item => item.PlannerIdentity == identity)
                        .Select(item => item.PlannerEvidence)
                        .Concat(goal.GateTestObligations.Where(item => item.PlannerIdentity == identity).Select(item => item.PlannerEvidence));
                    if (storedPayloads.Any(stored => !payload.SemanticallyEquals(stored)))
                        return $"Conflicting stored Planner evidence criterion={payload.CriterionIndex} key={payload.EvidenceKey}; re-emit the original payload or request a new brief.";
                }
                items.Add((directive, identity));
            }

            var selectorDiagnostic = PlannerCommandSelectorValidation.Refusal(
                items.Select(item => item.Directive)
                    .Where(directive => directive.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                        GateTestObligation.IsAcceptanceGateOwner(directive.EvidenceOwner))
                    .SelectMany(directive => directive.TestNames ?? []), goal, task, declaredGateCheckSource, listing);
            if (selectorDiagnostic is not null) return selectorDiagnostic;

            // Requests are not authority. Validate the entire ownership delta now,
            // but publish it only after the host accepts this Planner round.
            var ownershipDiagnostic = PlanOwnership(goal, task, batch, dispatchBriefVersion, clock.UtcNow, out _);
            if (ownershipDiagnostic is not null) return ownershipDiagnostic;

            foreach (var (directive, identity) in items)
            {
                if (directive.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                    GateTestObligation.IsAcceptanceGateOwner(directive.EvidenceOwner) && directive.TestNames is { Count: > 0 })
                {
                    var obligation = new GateTestObligation(identity?.StableId ?? directive.QuestionFingerprint,
                        dispatchBriefVersion ?? goal.AuthoritativeBrief.Version, directive.TestNames.ToArray(),
                        task.Id.Value, directive.Question, clock.UtcNow, identity, directive.PlannerEvidence,
                        RepositoryKey: directive.PlannerEvidence?.Repository);
                    goal.RecordGateTestObligation(obligation);
                    append(goal, task.Id, ProgressKind.TaskNote, obligation.RecordingNote);
                }
                else
                {
                    kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, directive.Question, kind: directive.Kind,
                        questionFingerprint: directive.QuestionFingerprint, blockerFingerprint: directive.BlockerFingerprint,
                        recordDuplicateSuppression: false, evidenceOwner: directive.EvidenceOwner,
                        storeReference: directive.StoreReference, testNames: directive.TestNames,
                        plannerIdentity: identity, plannerEvidence: directive.PlannerEvidence);
                }
            }
            // Evidence replays stay answered. An amendment batch never authorizes completion.
            foreach (var amendment in batch.Directives.Where(directive => directive.Kind == HumanWaitKind.PlannerBriefAmendment))
                kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, amendment.Question, kind: amendment.Kind,
                    questionFingerprint: amendment.QuestionFingerprint, blockerFingerprint: amendment.BlockerFingerprint,
                    recordDuplicateSuppression: false, amendmentCriterionIndex: amendment.AmendmentCriterionIndex,
                    amendmentProposedText: amendment.AmendmentProposedText);
            blocksActiveWork = kernel.GetPendingBlockingHumanInput(goal.Id).Any(item => item.TaskId == task.Id) ||
                batch.Directives.Any(directive => directive.Kind == HumanWaitKind.PlannerBriefAmendment);
        }
        return null;
    }

    internal static string? PlanOwnership(Goal goal, TaskSpec task, HumanInputDirectiveParseResult batch,
        int? briefVersion, DateTimeOffset recordedAt, out IReadOnlyList<CriterionEvidenceObligation> changes)
    {
        changes = [];
        if (batch.IsMalformed) return batch.Diagnostic;
        if (task.RequiredRole != AgentRole.Planner || !goal.Tasks.Contains(task))
            return "Planner ownership requires the goal's requesting Planner task.";
        // Legacy/unbound requests retain their request semantics, never exemption authority.
        if (briefVersion is null) return null;
        if (briefVersion != goal.AuthoritativeBrief.Version)
            return "Planner ownership belongs to an obsolete brief.";
        var sources = new List<PlannerCriterionEvidenceSource>();
        foreach (var directive in batch.Directives)
        {
            if (directive.Kind == HumanWaitKind.PlannerBriefAmendment) continue;
            if (directive.PlannerEvidence is not { } payload || payload.CriterionIndex <= 0 ||
                string.IsNullOrWhiteSpace(payload.EvidenceKey) || string.IsNullOrWhiteSpace(payload.Needed) ||
                string.IsNullOrWhiteSpace(payload.Reason) || goal.RefinedSpec is not { } spec ||
                payload.CriterionIndex > spec.AcceptanceCriteria.Count)
                return "Planner ownership requires a valid structured directive within the authoritative criteria.";
            if (!GateTestObligation.IsAcceptanceGateOwner(payload.Owner)) continue;
            if (payload.Availability != "post-implementation" || payload.Store is not null || payload.StoreReference is not null ||
                directive.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
                !GateTestObligation.IsAcceptanceGateOwner(directive.EvidenceOwner) ||
                (payload.Selectors is not null && (payload.Selectors.Count == 0 || payload.Selectors.Any(string.IsNullOrWhiteSpace))) ||
                !(payload.Selectors ?? []).SequenceEqual(directive.TestNames ?? [], StringComparer.Ordinal))
                return "Planner ownership requires a schema-valid post-implementation acceptance-gate directive.";
            sources.Add(new(new(goal.Id.Value, task.Id.Value, briefVersion.Value, payload.CriterionIndex, payload.EvidenceKey), payload));
        }
        return goal.PlanPlannerAcceptanceOwnership(sources, recordedAt, out changes);
    }
}

internal static class PlannerRoundRecovery
{
    internal static void RequeueAfterRevision(Goal goal, IEnumerable<HumanInputRequest> requests,
        IGoalLifecycleEventWriter eventWriter, IClock clock)
    {
        var briefVersion = goal.AuthoritativeBrief.Version;
        foreach (var task in goal.Tasks)
        {
            if (task.Status != WorkTaskStatus.WaitingForHuman ||
                !PlannerEvidenceRecorder.IsSupersededBriefPlannerRound(goal, task) ||
                task.LastDispatch!.BriefVersion >= briefVersion ||
                HasPlannerEvidenceOrAmendmentWait(goal, task, requests))
                continue;

            Requeue(goal, task, eventWriter, clock, SupersededNote(goal, task));
        }
    }

    internal static bool TryRequeueAfterAnswer(Goal goal, TaskSpec task, IGoalLifecycleEventWriter eventWriter,
        IClock clock, out bool premiseInvalid)
    {
        var note = GetAnswerRequeueNote(goal, task, out premiseInvalid);
        if (note is null) return false;
        Requeue(goal, task, eventWriter, clock, note);
        return true;
    }

    internal static string? GetAnswerRequeueNote(Goal goal, TaskSpec task, out bool premiseInvalid)
    {
        premiseInvalid = false;
        if (task.RequiredRole != AgentRole.Planner) return null;

        var processRunning = task.LastProcess is { IsRunning: true };
        if (!processRunning && PlannerEvidenceRecorder.IsObsoletePlannerEvidenceRound(goal, task))
            return "Historical Planner evidence retained; planning must run against the current brief.";

        if (PlannerEvidenceRecorder.IsSupersededBriefPlannerRound(goal, task))
            return SupersededNote(goal, task);

        premiseInvalid = WorkerResultBlockers.TryFindPremiseInvalidEvidence(task.LastVerification, out _);
        return premiseInvalid && !processRunning
            ? "Planner reported a premise-invalid blocker; planning must rerun with the human answer."
            : null;
    }

    internal static void EnsureDispatchAllowed(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester or AgentRole.Reviewer) ||
            goal.AuthoritativeRefinedSpecVersion is not { } specification ||
            specification.BriefVersion >= goal.AuthoritativeBrief.Version)
            return;

        var unfinishedPlanner = goal.Tasks.FirstOrDefault(planner => planner.RequiredRole == AgentRole.Planner &&
            (planner.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman) &&
            (planner.LastDispatch is not null || planner.DispatchHistory.Count > 0));
        if (unfinishedPlanner is not null)
        {
            throw new InvalidOperationException(
                $"Task '{task.Id}' cannot dispatch with specification brief v{specification.BriefVersion} and current brief v{goal.AuthoritativeBrief.Version} while Planner task '{unfinishedPlanner.Id}' is {unfinishedPlanner.Status}.");
        }
    }

    private static bool HasPlannerEvidenceOrAmendmentWait(Goal goal, TaskSpec task, IEnumerable<HumanInputRequest> requests) =>
        task.LastVerification?.PlannerEvidenceBatch?.Directives.Any(directive =>
            directive.PlannerEvidence is not null || directive.Kind == HumanWaitKind.PlannerBriefAmendment) == true ||
        requests.Any(request => request.GoalId == goal.Id && request.TaskId == task.Id &&
            ((!request.IsCompleted && request.PlannerIdentity is not null) ||
             (request.Kind == HumanWaitKind.PlannerBriefAmendment && !request.AmendmentReplanRouted)));

    private static string SupersededNote(Goal goal, TaskSpec task) =>
        $"Planner must re-plan: dispatch brief v{task.LastDispatch!.BriefVersion} was superseded by current brief v{goal.AuthoritativeBrief.Version}.";

    private static void Requeue(Goal goal, TaskSpec task, IGoalLifecycleEventWriter eventWriter, IClock clock, string note)
    {
        task.ClearLatestVerification();
        TaskStatusTransitionRecorder.Record(eventWriter, goal.Id, task.SetStatus(WorkTaskStatus.Assigned), TaskStatusTransitionRecorder.GoalLifecycleRestoreTaskAfterHumanInputObsoletePlanner);
        var progress = new ProgressEvent(goal.Id, task.Id, ProgressKind.TaskNote, note, clock.UtcNow);
        goal.Append(progress);
        eventWriter.AppendTimelineEvent(progress);
    }
}
