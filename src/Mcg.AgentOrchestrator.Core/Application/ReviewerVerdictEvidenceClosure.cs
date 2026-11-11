namespace Mcg.AgentOrchestrator.Core;

// Owns closure of worker-owned Planner waits from current-candidate Reviewer verdicts.
internal sealed class ReviewerVerdictEvidenceClosure(
    object requestLock,
    IClock clock,
    Action<Goal, TaskId?, ProgressKind, string> append)
{
    internal void Close(Goal goal, TaskSpec reviewer, TaskVerificationRecord verification,
        IEnumerable<HumanInputRequest> requests)
    {
        lock (requestLock)
        {
            var pending = requests.Where(request =>
            {
                var owner = request.EvidenceOwner?.Trim();
                return request.GoalId == goal.Id && !request.IsCompleted &&
                    request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                    (string.Equals(owner, "reviewer", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "worker", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "developer", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "tester", StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            foreach (var request in pending)
            {
                var owner = request.EvidenceOwner!.Trim().ToLowerInvariant();
                var reason = GetSkipReason(goal, reviewer, verification, request, out var verdict, out var developer,
                    out var developerCandidate);
                string? reportTests = null;
                if (reason == "verdict-not-met" && owner == "developer" &&
                    !UnnamedDeveloperReportEvidence.IsNotMet(verification, request.PlannerIdentity!.CriterionIndex - 1) &&
                    ResolveCriterion(goal, request.PlannerIdentity!) is { } criterion)
                {
                    if (DeveloperReportEvidence.TryMatch(criterion, developer!, out reportTests, out var reportReason))
                        reason = null;
                    else
                        reason = reportReason ?? reason;
                }
                var ownerTask = developer;
                if (reason is null && owner == "tester")
                    reason = GetTesterSkipReason(goal, developer!.LastDispatch!.ResultCommit!, out ownerTask);
                if (reason is not null)
                {
                    var prefix = request.Id.Value[..Math.Min(8, request.Id.Value.Length)];
                    append(goal, reviewer.Id, ProgressKind.TaskNote,
                        $"REVIEWER_VERDICT_EVIDENCE_SKIPPED request={prefix} reason={reason}");
                    continue;
                }

                string answer;
                if (reportTests is not null)
                {
                    var log = string.IsNullOrWhiteSpace(developer!.LastVerification!.StandardOutputPath)
                        ? "unavailable" : developer.LastVerification.StandardOutputPath;
                    var candidate = developerCandidate is null ? developer.LastDispatch!.ResultCommit : verification.ReviewedCommit;
                    answer = $"DEVELOPER_REPORT_EVIDENCE task={developer.Id.Value} " +
                        $"criterion_index={request.PlannerIdentity!.CriterionIndex - 1} reviewer_task={reviewer.Id.Value} " +
                        $"candidate={candidate} worker_result_log={log} " +
                        $"tests={reportTests[..Math.Min(400, reportTests.Length)]}";
                }
                else
                {
                    var evidence = verdict!.Evidence[..Math.Min(400, verdict.Evidence.Length)];
                    var log = string.IsNullOrWhiteSpace(verification.StandardOutputPath)
                        ? "unavailable" : verification.StandardOutputPath;
                    var ownerFields = owner == "reviewer" ? string.Empty : $" owner={owner} owner_task={ownerTask!.Id.Value}";
                    answer = ($"REVIEWER_VERDICT_EVIDENCE task={reviewer.Id.Value} " +
                        $"criterion_index={verdict.CriterionIndex}{ownerFields} candidate={verification.ReviewedCommit} " +
                        $"worker_result_log={log} evidence={evidence}").Trim();
                }
                if (developerCandidate is not null)
                    answer += $" developer_candidate={developerCandidate}";
                request.Complete(answer, clock.UtcNow, origin: HumanInputAnswerOrigin.Worker,
                    briefVersion: goal.AuthoritativeBrief.Version);
                append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer);
            }
        }
    }

    private static string? ResolveCriterion(Goal goal, PlannerEvidenceIdentity identity)
    {
        var version = goal.AuthoritativeRefinedSpecVersion;
        var index = identity.CriterionIndex - 1;
        if (version is null || version.BriefVersion != identity.BriefVersion ||
            index < 0 || index >= version.Spec.AcceptanceCriteria.Count)
            return null;
        var criterion = version.Spec.AcceptanceCriteria[index];
        return string.IsNullOrWhiteSpace(criterion) ? null : criterion;
    }

    private static string? GetSkipReason(Goal goal, TaskSpec reviewer, TaskVerificationRecord verification,
        HumanInputRequest request, out CriterionVerdict? verdict, out TaskSpec? developer, out string? developerCandidate)
    {
        verdict = null;
        developer = null;
        developerCandidate = null;
        if (request.PlannerIdentity is not { } identity)
            return "untyped";
        if (identity.BriefVersion != goal.AuthoritativeBrief.Version ||
            identity.BriefVersion != reviewer.LastDispatch?.BriefVersion)
            return "brief-version-differs";
        if (!WorkerResultBlockers.TryFindPassVerdict(verification) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None)
            return "review-not-pass";

        developer = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        var candidate = developer?.LastDispatch?.ResultCommit;
        if (developer is null || developer.Status != WorkTaskStatus.Completed || string.IsNullOrWhiteSpace(candidate))
            return "candidate-unknown";
        var tester = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester &&
                task.Status == WorkTaskStatus.Completed && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        if (tester?.LastDispatch is { } dispatch &&
            dispatch.DispatchedAt > developer.LastDispatch!.DispatchedAt &&
            string.Equals(dispatch.BaseCommit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            developerCandidate = candidate;
            candidate = dispatch.ResultCommit.Trim();
        }
        if (!string.Equals(verification.ReviewedCommit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
        {
            var reviewed = string.IsNullOrWhiteSpace(verification.ReviewedCommit) ? "unavailable" : verification.ReviewedCommit.Trim();
            return $"candidate-differs reviewed={reviewed} expected={candidate}";
        }
        return CriterionSkipReason(goal, request, verification, out verdict) is null ? null : "verdict-not-met";
    }

    // Shared by round-report closure: an overall pass cannot replace this criterion's attestation.
    internal static string? CriterionSkipReason(Goal goal, HumanInputRequest request,
        TaskVerificationRecord verification, out CriterionVerdict? verdict)
    {
        verdict = null;
        if (request.PlannerIdentity is not { } identity ||
            ReportCriterionLookup.Find(goal, identity, out var index) is null)
            return "criterion-unresolved";
        if (identity.GoalId != goal.Id.Value || identity.RequestingTaskId != request.TaskId?.Value ||
            request.PlannerEvidence is not { } payload || payload.CriterionIndex != identity.CriterionIndex ||
            payload.EvidenceKey != identity.EvidenceKey ||
            !string.Equals(payload.Owner?.Trim(), request.EvidenceOwner?.Trim(), StringComparison.OrdinalIgnoreCase))
            return "criterion-binding-differs";
        if (!WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out var diagnostic))
            return $"criterion-verdict-invalid:{diagnostic}";
        var matches = verdicts.Where(item => item.CriterionIndex == index).ToArray();
        if (matches.Length == 0) return $"criterion-verdict-missing:criterion_index={index}";
        if (matches.Length != 1)
            return $"criterion-verdict-conflict:criterion_index={index} verdicts={string.Join(",", matches.Select(item => item.Verdict))}";
        if (matches[0].Verdict != "met")
            return $"criterion-verdict-not-met:criterion_index={index} verdict={matches[0].Verdict}";
        verdict = matches[0];
        return null;
    }

    private static string? GetTesterSkipReason(Goal goal, string candidate, out TaskSpec? tester)
    {
        tester = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        if (tester is null)
            return "owner-round-missing";
        if (tester.Status != WorkTaskStatus.Completed)
            return "owner-round-incomplete";
        return string.Equals(tester.LastVerification?.ReviewedCommit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
            ? null : "owner-candidate-differs";
    }
}
