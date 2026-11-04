using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A review consumes a particular delivery, not every receipt on a task or every verification event.
// All authority comes from retained dispatches, parsed completions and the existing receipt selector.
internal static class ReviewerPassingFindingEvidenceRouter
{
    internal static bool TryObserve(ConductorDriver driver, Goal goal, TaskSpec task,
        ConductorAutonomyPolicy policy, out FailedGoalFindingObservation observation)
    {
        observation = FailedGoalFindingObservation.None;
        if (task.RequiredRole != AgentRole.Reviewer) return false;
        var candidate = driver._getPreReviewEvidenceContext(goal).CandidateSha?.Trim();
        if (!ConductorGitRevisionReader.IsValid(candidate)) return false;
        var settings = driver._getFindingEvidenceEngineSettings(goal);
        return TryDecide(goal, task, goal.Tasks.TakeWhile(item => item.Id != task.Id)
                .LastOrDefault(item => item.RequiredRole == AgentRole.Developer), candidate!,
            request => TryNormalizeFindingEvidenceRequest(request, settings,
                (project, testClass) => driver._resolveFindingEvidenceSiblingClasses(goal, project, testClass),
                out var normalized, out _, out _, out _) ? normalized : null,
            BuildFindingEvidenceExecutionBasisIdentity(settings),
            ReviewRetryCapReceipt.Create(goal, policy.ReviewAutoRetryStopRound, policy.ReviewAutoRetryLifetimeMultiplier),
            out observation, driver._resolveCandidateIdentity(goal).Identity,
            PreTesterCoverageComposition.ReadCoverage(driver, goal, candidate!, BuildFindingEvidenceExecutionBasisIdentity(settings)));
    }

    internal static bool TryDecide(Goal goal, TaskSpec task, TaskSpec? developer, string candidate,
        Func<FindingEvidenceRequest, FindingEvidenceRequest?> normalize, string executionBasis,
        ReviewRetryCapReceipt cap, out FailedGoalFindingObservation observation, CandidateIdentity? currentIdentity = null,
        IReadOnlyList<PreTesterCoverageComposition.PreTesterCoverageSource>? preTesterCoverage = null)
    {
        observation = FailedGoalFindingObservation.None;
        if (task.RequiredRole != AgentRole.Reviewer || !ConductorGitRevisionReader.IsValid(candidate) ||
            !string.Equals(task.LastVerification?.ReviewedCommit, candidate, StringComparison.OrdinalIgnoreCase) ||
            task.LastVerification?.ReviewFindingContractViolation is not null ||
            !WorkerResultBlockers.TryFindReviewFindingRound(task.LastVerification, out var current, out _)) return false;
        if (currentIdentity is not null && task.LastVerification!.CandidateIdentity is { } reviewedIdentity &&
            !CandidateIdentity.AreSameCandidate(currentIdentity, reviewedIdentity)) return false;
        var findings = ReviewFindings.GetOpenBlockingFindings(
            task.LastVerification!.MergedReviewFindings ?? current.Findings, goal.EffectiveAcceptanceCriteriaCorrections);
        var episodes = new List<Episode>();
        var fresh = new List<(ReviewFinding Finding, IReadOnlyList<FindingEvidenceReceipt> Receipts)>();
        foreach (var finding in findings.Where(item => item.EvidenceRequest is not null))
        {
            var request = normalize(finding.EvidenceRequest!);
            // A pending, expanded, negative or unqualified request keeps its existing execution path.
            if (request is null || !TryResolveFindingEvidenceCoverage(task, request, candidate, executionBasis,
                    out var receipts, out var uncovered, out _, preTesterCoverage) || uncovered.Count > 0) return false;
            var requestId = BuildFindingEvidenceIdentity(request);
            var proof = FindCompletedReview(goal, task, finding.StableId, candidate, requestId, receipts, normalize);
            if (proof is null && FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                    goal.Timeline, task.Id, candidate, finding.StableId) < 2 && !cap.IsAtCap)
            {
                fresh.Add((finding, receipts));
                continue;
            }
            var receiptIds = receipts.Select(receipt => receipt.ReceiptId).Order(StringComparer.Ordinal).ToArray();
            var key = FindingEvidenceReviewContext.Hash($"{task.Id}:{candidate.ToLowerInvariant()}:{finding.StableId}:" +
                requestId + ":" + string.Join(',', receiptIds));
            episodes.Add(new(finding, key, requestId, receiptIds, proof));
        }
        if (episodes.Count == 0) return false;
        if (fresh.Count > 0)
        {
            // Independent requests retain their entitlement. Do not charge another delivery
            // against an already consumed finding when delivering this material request progress.
            observation = BuildCappedFindingEvidenceDeliveryRetry(goal, task, candidate,
                fresh.Select(item => item.Finding).ToArray(),
                fresh.SelectMany(item => item.Receipts).Select(item => item.ReceiptId).Distinct(StringComparer.Ordinal).ToArray(),
                "New qualified evidence for distinct finding requests; previously consumed findings retain their own correction/hold state.");
            return true;
        }

        var evidence = $"reviewer_task={task.Id}; candidate_sha={candidate}; " +
            $"review_budget={cap.Round}/{cap.StopRound}; lifetime_budget={cap.LifetimeRound}/{cap.LifetimeBackstop}; " +
            string.Join(" | ", episodes.Select(item =>
                $"episode_key={item.Key}; finding_id={item.Finding.StableId}; request_identity={item.RequestId}; " +
                $"receipt_ids={string.Join(',', item.ReceiptIds)}; review_dispatch={item.Proof?.DispatchAt.ToString("O") ?? "unproven"}; " +
                $"review_completed={item.Proof?.CompletedAt.ToString("O") ?? "unproven"}; anchor={item.Finding.Location}; remaining_change={item.Finding.Description}"));
        var dispositions = episodes.Select(item => (Episode: item, Reason:
            AlreadyCorrected(goal, task, candidate, item) ? "correction-already-routed"
            : cap.IsAtCap ? "correction-budget-exhausted"
            : FindingEvidenceExecutionClassifier.CountEvidenceDeliveryRetries(
                goal.Timeline, task.Id, candidate, item.Finding.StableId) >= 2 ? "delivery-budget-exhausted"
            : developer is null ? "developer-target-unavailable"
            : !HasActionableChange(item.Finding) ? "actionable-change-unavailable" : null)).ToArray();
        var routed = dispositions.Where(item => item.Reason is null).Select(item => item.Episode).ToArray();
        var held = string.Join(',', dispositions.Where(item => item.Reason is not null)
            .Select(item => item.Episode.Finding.StableId + ":" + item.Reason));
        if (routed.Length == 0)
        {
            var reason = string.Join(',', dispositions.Select(item => item.Reason).Distinct(StringComparer.Ordinal));
            observation = FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                $"REVIEWER_PASSING_EVIDENCE_HOLD reason={reason}; held_finding_ids={held}; {evidence}; findings remain open.") with
            { Attribution = "reviewer-passing-evidence:" + reason };
            return true;
        }

        observation = FailedGoalFindingObservation.Routed(FailedGoalFindingObservationKind.FindingRouteObserved,
            developer!.Id, FailedGoalFindingObservation.BuildAttemptIdentity(developer),
            "auto-review-retry REVIEWER_PASSING_EVIDENCE_CORRECTION " +
            string.Concat(routed.Select(item => $"correction_episode={item.Key}; ")) +
            $"routed_finding_ids={string.Join(',', routed.Select(item => item.Finding.StableId))}; held_finding_ids={held}; {evidence}; " +
            "The completed Reviewer retained these findings after delivery. Repair only the routed findings' source/test changes; " +
            "passing class execution does not establish missing coverage or close the finding.", null,
            observedCause: RetryCause.NewSourceFinding);
        return true;
    }

    private static ReviewProof? FindCompletedReview(Goal goal, TaskSpec task, string findingId, string candidate,
        string requestId, IReadOnlyList<FindingEvidenceReceipt> receipts,
        Func<FindingEvidenceRequest, FindingEvidenceRequest?> normalize)
    {
        foreach (var record in task.VerificationHistory.Reverse())
        {
            // Receipt enrichment copies a worker result. Only dispatch-sourced completion of the
            // matching invocation can consume delivery, and it must itself re-raise the same request.
            if (record.EvidenceSource == VerificationEvidenceSource.Manual || !record.WorkerResultPresent ||
                record.ExitCode != 0 || record.ChildExitCode is not (null or 0) ||
                record.ProviderFailureKind != ProviderFailureKind.Unknown ||
                !string.Equals(record.ReviewedCommit, candidate, StringComparison.OrdinalIgnoreCase) ||
                !WorkerResultBlockers.TryFindReviewFindingRound(record, out var round, out _)) continue;
            var retained = round.Findings.SingleOrDefault(item => item.StableId == findingId &&
                item.State == ReviewFindingState.Open && item.Severity != FindingSeverity.Advisory);
            if (retained?.EvidenceRequest is null || normalize(retained.EvidenceRequest) is not { } priorRequest ||
                BuildFindingEvidenceIdentity(priorRequest) != requestId) continue;
            var dispatchAt = record.DispatchAttemptAt ?? record.DispatchStartedAt;
            if (dispatchAt is null || record.CompletedAt < dispatchAt) continue;
            var dispatch = task.DispatchHistory.SingleOrDefault(item => item.DispatchedAt == dispatchAt && item.GoalId == goal.Id);
            if (dispatch is null || !string.Equals(dispatch.BaseCommit, candidate, StringComparison.OrdinalIgnoreCase)) continue;
            if (record.CandidateIdentity is not null && task.LastVerification?.CandidateIdentity is not null &&
                !CandidateIdentity.AreSameCandidate(record.CandidateIdentity, task.LastVerification.CandidateIdentity)) continue;
            if (record.EvidenceSource == VerificationEvidenceSource.Unspecified &&
                !goal.Timeline.Any(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskVerificationRecorded &&
                    item.OccurredAt >= record.CompletedAt && item.Message.StartsWith("Dispatch execution ", StringComparison.Ordinal) &&
                    item.Message.EndsWith(": " + record.Command, StringComparison.Ordinal) &&
                    !task.DispatchHistory.Any(later => later.DispatchedAt > dispatchAt && later.DispatchedAt <= item.OccurredAt)))
                continue;
            if (dispatch.FindingEvidenceReviewContext is { } context &&
                (context.GoalId != goal.Id.Value || context.TaskId != task.Id.Value || context.DispatchAt != dispatchAt ||
                 !string.Equals(context.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) ||
                 receipts.Any(receipt => !context.ReceiptIds.Contains(receipt.ReceiptId, StringComparer.Ordinal)))) continue;
            // This also supports legacy dispatches without context, but only with exact ordered
            // delivery and parsed completion proof. A generic verification event proves neither.
            var continuationDelivered = dispatch.FindingEvidenceReviewContext is not null &&
                dispatch.FindingEvidenceReviewContinuation is { } continuation &&
                continuation.GoalId == goal.Id.Value && continuation.TaskId == task.Id.Value &&
                string.Equals(continuation.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase) &&
                (record.CandidateIdentity is null || CandidateIdentity.AreSameCandidate(record.CandidateIdentity, continuation.CandidateIdentity)) &&
                continuation.PriorVerificationAt < dispatchAt && continuation.PriorDispatchAt < dispatchAt &&
                receipts.All(receipt => continuation.Selections.Any(selection =>
                    selection.FindingId == findingId && selection.RequestIdentity == requestId &&
                    selection.ReceiptId == receipt.ReceiptId && selection.SourceVerificationAt < dispatchAt &&
                    selection.ExecutionIdentity == FindingEvidenceReviewContext.ExecutionIdentity(receipt)));
            if (!continuationDelivered && !goal.Timeline.Any(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried &&
                    item.OccurredAt <= dispatchAt &&
                    FindingEvidenceExecutionClassifier.TryReadEvidenceDeliveryRetry(item, out var sha, out var ids) &&
                    string.Equals(sha, candidate, StringComparison.OrdinalIgnoreCase) && ids.Contains(findingId, StringComparer.Ordinal) &&
                    receipts.All(receipt => Field(item.Message, "receipt_ids").Split(',').Contains(receipt.ReceiptId, StringComparer.Ordinal))))
                continue;
            return new(dispatchAt.Value, record.CompletedAt);
        }
        return null;
    }

    private static bool AlreadyCorrected(Goal goal, TaskSpec requester, string candidate, Episode episode) =>
        goal.Timeline.Any(item => item.Kind == ProgressKind.TaskRetried && item.TaskId != requester.Id &&
            goal.Tasks.Any(target => target.Id == item.TaskId && target.RequiredRole == AgentRole.Developer) &&
            (item.Message.Contains("correction_episode=" + episode.Key + ";", StringComparison.Ordinal) ||
             item.Message.Contains("candidate_sha=" + candidate + ";", StringComparison.OrdinalIgnoreCase) &&
             item.Message.Contains("stable_id: " + episode.Finding.StableId + ";", StringComparison.Ordinal) &&
             episode.ReceiptIds.All(id => Field(item.Message, "receipt_ids").Split(',').Contains(id, StringComparer.Ordinal))));

    // Conservative source actionability: an owned anchor plus a concrete scenario/defect or
    // requested change. A location alone or disagreement with green evidence cannot authorize repair.
    private static bool HasActionableChange(ReviewFinding finding) =>
        ReviewFindingRouting.Project([finding], finding.Description).Single() is
            { TargetRole: AgentRole.Developer, EscalateToOperator: false } &&
        !string.IsNullOrWhiteSpace(finding.Location.Region) &&
        (RepositoryOwnershipMap.Classify(finding.Location.File).Area is
            RepositoryOwnershipArea.Source or RepositoryOwnershipArea.Test or RepositoryOwnershipArea.SharedInfrastructure) &&
        Regex.IsMatch(finding.Description, @"(?i)\b(missing|skips?|only|regardless|drops?|loses?|does not|must|add|restore|preserve|suppress|retain|handle|reject|leaks?)\b") &&
        Regex.IsMatch(finding.Description, @"(?i)(`[^`]+`|\b(when|if|during|after|before|resumption|scenario|regression|coverage)\b)");

    private static string Field(string message, string name) => message.Split(';')
        .Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith(name + "=", StringComparison.Ordinal))?
        [(name.Length + 1)..] ?? "";

    private sealed record ReviewProof(DateTimeOffset DispatchAt, DateTimeOffset CompletedAt);
    private sealed record Episode(ReviewFinding Finding, string Key, string RequestId,
        IReadOnlyList<string> ReceiptIds, ReviewProof? Proof);
}
