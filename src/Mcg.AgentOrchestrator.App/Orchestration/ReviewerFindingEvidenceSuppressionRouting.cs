using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ReviewerFindingEvidenceSuppressionRoute(
    string[] WritableBlockerIds,
    AgentRole? ChosenOwner,
    bool DeferToReviewRetryRoute);

internal static class ReviewerFindingEvidenceSuppressionRouting
{
    internal static ReviewFinding[] Apply(
        ConductorDriver driver, Goal goal, TaskSpec requestingTask,
        IReadOnlyList<ReviewFinding> roundFindings, ReviewFinding[] requestingFindings,
        string? candidateSha, FindingEvidenceBatchIdentity batch,
        Action<GoalId, TaskId, IReadOnlyList<FindingEvidenceRequestDisposition>> recordDispositions,
        Action<GoalId, TaskId, string, IReadOnlyList<string>, string, AgentRole, string, string> recordSuppression)
    {
        var candidateShaAvailable = ConductorGitRevisionReader.IsValid(candidateSha);
        var openBlockingFindings = ReviewFindings.GetOpenBlockingFindings(
            roundFindings, goal.EffectiveAcceptanceCriteriaCorrections);
        var route = Resolve(requestingTask.LastVerification, goal.EffectiveAcceptanceCriteriaCorrections, openBlockingFindings);
        if (route.DeferToReviewRetryRoute)
        {
            FindingRoundBatchCoordinator.RecordDeferred(driver, recordDispositions, goal, requestingTask,
                requestingFindings, candidateShaAvailable ? candidateSha : null, batch.RoundFingerprint,
                batch.ExecutionBasisIdentity, "legacy-review-retry-ownership");
            return [];
        }

        var writableBlockerIds = route.WritableBlockerIds;
        var reviewedCandidateSha = requestingTask.LastVerification?.ReviewedCommit?.Trim();
        var requestTargetsCurrentCandidate =
            !candidateShaAvailable ||
            !ConductorGitRevisionReader.IsValid(reviewedCandidateSha) ||
            string.Equals(candidateSha, reviewedCandidateSha, StringComparison.OrdinalIgnoreCase);
        if (writableBlockerIds.Length == 0 || !requestTargetsCurrentCandidate)
            return requestingFindings;

        var reason = candidateShaAvailable ? "unresolved-writable-blockers-on-unchanged-candidate"
            : "candidate-sha-unavailable-with-unresolved-writable-blockers";
        var remaining = SelectAndSuppress(
            writableBlockerIds, requestingFindings, requestingTask, candidateSha,
            ConductorDriver.BuildFindingEvidenceIdentity, requestIdentity => recordSuppression(
                goal.Id, requestingTask.Id, batch.CandidateSha, writableBlockerIds,
                CreateFindingEvidenceRequestId(requestIdentity),
                route.ChosenOwner ?? throw new InvalidOperationException("Writable finding suppression requires a feasible upstream owner."),
                reason,
                CreateFindingEvidenceSuppressionIdentity(batch.CandidateSha, requestIdentity, writableBlockerIds)));
        FindingRoundBatchCoordinator.RecordDeferred(driver, recordDispositions, goal, requestingTask,
            requestingFindings.Except(remaining).ToArray(), candidateShaAvailable ? candidateSha : null,
            batch.RoundFingerprint, batch.ExecutionBasisIdentity, reason);
        return remaining;
    }

    private static string CreateFindingEvidenceRequestId(string identity) =>
        "evidence-request-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];

    private static string CreateFindingEvidenceSuppressionIdentity(
        string candidateSha,
        string requestIdentity,
        IReadOnlyList<string> writableBlockerIds)
    {
        var blockerIdentity = string.Join(
            "\u001f",
            writableBlockerIds
                .OrderBy(id => id, StringComparer.Ordinal));
        return "evidence-suppression-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{candidateSha.Trim().ToLowerInvariant()}\u001e{requestIdentity}\u001e{blockerIdentity}")))
            .ToLowerInvariant()[..24];
    }

    internal static ReviewFinding[] SelectAndSuppress(
        IReadOnlyList<string> writableBlockerIds,
        ReviewFinding[] requestingFindings,
        TaskSpec requestingTask,
        string? candidateSha,
        Func<FindingEvidenceRequest, string> requestIdentity,
        Action<string> recordSuppression)
    {
        var selfExempt = SelectSelfExemptRequestingFindings(
            writableBlockerIds, requestingFindings, requestingTask, candidateSha);
        foreach (var identity in requestingFindings.Except(selfExempt)
                     .Select(finding => requestIdentity(finding.EvidenceRequest!))
                     .Distinct(StringComparer.Ordinal))
        {
            recordSuppression(identity);
        }

        return selfExempt;
    }

    internal static ReviewFinding[] SelectSelfExemptRequestingFindings(
        IReadOnlyList<string> writableBlockerIds,
        IReadOnlyList<ReviewFinding> requestingFindings,
        TaskSpec requestingTask,
        string? candidateSha) =>
        requestingFindings.Where(finding =>
            finding.State == ReviewFindingState.Open &&
            finding.EvidenceRequest is { Selections.Count: > 0 } &&
            !string.IsNullOrWhiteSpace(candidateSha) &&
            string.Equals(requestingTask.LastVerification?.ReviewedCommit, candidateSha,
                StringComparison.OrdinalIgnoreCase) &&
            FindingEvidenceExecutionClassifier.Classify(requestingTask, finding, candidateSha) ==
                FindingEvidenceExecutionState.PendingExecution &&
            writableBlockerIds.All(id => string.Equals(id, finding.StableId, StringComparison.Ordinal)))
            .ToArray();

    internal static ReviewerFindingEvidenceSuppressionRoute Resolve(
        TaskVerificationRecord? verification,
        IReadOnlyList<EffectiveAcceptanceCriteriaCorrection> criteriaCorrections,
        IReadOnlyList<ReviewFinding> openBlockingFindings)
    {
        WorkerResultBlockers.TryFindUnsuppressedNeedsWorkVerdict(
            verification,
            criteriaCorrections,
            out var blockerProse,
            out _);
        if (string.IsNullOrWhiteSpace(blockerProse))
        {
            blockerProse = string.Join("; ", openBlockingFindings.Select(finding => finding.Description));
        }

        var route = ReviewFindingRouting.Resolve(openBlockingFindings, blockerProse);
        var allUseLegacyOwnership = openBlockingFindings.Count > 0 &&
            openBlockingFindings.All(finding => finding.Category == FindingCategory.Unspecified);
        if (allUseLegacyOwnership &&
            (route.EscalateToOperator || route.TargetRole != AgentRole.Developer))
        {
            return new ReviewerFindingEvidenceSuppressionRoute([], route.TargetRole, true);
        }

        var writableBlockerIds = route.TargetRole == AgentRole.Developer
            ? ReviewFindingRouting.Project(openBlockingFindings, blockerProse)
                .Where(projection => projection.TargetRole == route.TargetRole)
                .Select(projection => projection.Finding.StableId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
            : [];
        return new ReviewerFindingEvidenceSuppressionRoute(
            writableBlockerIds,
            route.TargetRole,
            false);
    }
}
