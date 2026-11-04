using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Describes one validated approval-only hold against the already observed candidate snapshot.
// It owns no admission decision or cross-tick state.
internal static class PassedCohortApprovalOverlapObservation
{
    internal const string Prefix = "COHORT_APPROVAL_OVERLAP ";
    private const int PreviewLimit = 10;

    internal static string Format(PassedCohortReceiptApprovalHold.Decision hold,
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates, DateTimeOffset observedAt)
    {
        var receipt = hold.Receipt;
        var identity = receipt.Identity;
        var memberIds = identity.Members.Select(member => member.GoalId).ToHashSet();
        // Order before case-insensitive deduplication to choose the smallest receipt-side spelling.
        var paths = identity.Members.SelectMany(member => member.LandingPaths)
            .Select(RepositoryPathOverlap.Normalize).Order(StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var overlaps = candidates
            .Where(candidate => !memberIds.Contains(candidate.GoalId))
            .Select(candidate => candidate.ProjectionResult)
            .OfType<GateReadyCandidateProjectionResult.Ready>()
            .Select(ready => ready.Projection)
            .Where(projection => string.Equals(projection.MainRevision, identity.ObservedMainRevision,
                StringComparison.Ordinal))
            .Select(projection => new
            {
                projection.GoalId,
                projection.CandidateRevision,
                SharedPaths = paths.Where(path => projection.LandingPaths.Any(other =>
                    RepositoryPathOverlap.Overlaps(path, other))).ToArray()
            })
            .Where(overlap => overlap.SharedPaths.Length > 0)
            .OrderBy(overlap => overlap.GoalId.Value, StringComparer.Ordinal).ToArray();

        return Prefix + JsonSerializer.Serialize(new
        {
            receiptIdentity = new
            {
                receiptId = receipt.ReceiptId,
                version = AcceptanceCohortIdentity.Version,
                value = identity.Value,
                observedMainRevision = identity.ObservedMainRevision,
                combinedTreeRevision = identity.CombinedTreeRevision,
                manifestIdentity = identity.ManifestIdentity,
                members = identity.Members.Select(member => new
                {
                    goalId = member.GoalId.Value,
                    branchRevision = member.BranchRevision,
                    candidateRevision = member.CandidateRevision,
                    landingPaths = member.LandingPaths,
                    resourceKeys = member.ResourceKeys,
                    changeRiskTier = member.ChangeRiskTier.ToString(),
                    autoPromotionDisposition = member.AutoPromotionDisposition.ToString(),
                    mergeStatus = member.MergeStatus,
                    mergeReason = member.MergeReason,
                    briefVersion = member.BriefVersion
                })
            },
            heldMemberIds = memberIds.Select(id => id.Value).Order(StringComparer.Ordinal),
            missingApprovalIds = hold.MissingApprovals.Select(item => item.GoalId.Value).Order(StringComparer.Ordinal),
            completedAt = receipt.CompletedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            receiptAgeSeconds = Math.Max(0L, (observedAt - receipt.CompletedAt).Ticks / TimeSpan.TicksPerSecond),
            totalOverlapGoals = overlaps.Length,
            omittedGoalCount = Math.Max(0, overlaps.Length - PreviewLimit),
            overlaps = overlaps.Take(PreviewLimit).Select(overlap => new
            {
                goalId = overlap.GoalId.Value,
                candidateRevision = overlap.CandidateRevision,
                sharedPathCount = overlap.SharedPaths.Length,
                sharedPaths = overlap.SharedPaths.Take(PreviewLimit),
                omittedPathCount = Math.Max(0, overlap.SharedPaths.Length - PreviewLimit)
            })
        });
    }
}
