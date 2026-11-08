using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Candidate/baseline comparison is independent of conductor lifecycle state.
internal static class FindingEvidenceCandidateAttribution
{
    internal static ActionableCandidateRedAttribution? TryAttribute(
        string candidateSha, IReadOnlyList<FindingEvidenceArmReceipt> arms, FindingEvidenceBatch batch,
        Func<IReadOnlyList<string>, bool> everyFailingTestIsInsideCandidateChanges)
    {
        var candidate = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate);
        var baseline = arms.SingleOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        if (candidate is not { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red,
                FailingTestIdentities.Count: > 0 } ||
            !string.Equals(candidate.Sha, candidateSha, StringComparison.OrdinalIgnoreCase) ||
            (baseline is null && !everyFailingTestIsInsideCandidateChanges(candidate.FailingTestIdentities)))
            return null;

        var (failures, baselineFailures) = CandidateRedBaselineInheritance.AttributableFailures(candidate, baseline);
        if (failures is null) return null;
        var attributableFindings = new List<ReviewFinding>();
        var attributableFailingTests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in batch.Members)
        {
            var matchingFailures = batch.Members.Count == 1
                ? failures
                : failures.Where(failingIdentity => member.TypedRequest.Selections.Any(selection =>
                    FindingEvidenceFailureSelectionMatch.Matches(failingIdentity, selection.TestClass.Trim()))).ToArray();
            if (matchingFailures is not { Count: > 0 }) continue;

            var developerFindings = member.Findings.Where(finding =>
                finding.Category is not (FindingCategory.OperatorOwned or FindingCategory.SpecDefect or FindingCategory.AcceptanceOwned) &&
                IsDeveloperOwnedFindingAnchor(finding.Location.File));
            foreach (var finding in developerFindings)
            {
                attributableFindings.Add(finding);
                attributableFailingTests.UnionWith(matchingFailures);
            }
        }
        return attributableFindings.Count == 0 ? null : new ActionableCandidateRedAttribution(
            attributableFindings.DistinctBy(finding => finding.StableId).ToArray(),
            attributableFailingTests.ToArray(), candidate.TestResultPaths ?? [], baselineFailures);
    }

    private static bool IsDeveloperOwnedFindingAnchor(string path) =>
        RepositoryOwnershipMap.Classify(path).Area is RepositoryOwnershipArea.Source or
            RepositoryOwnershipArea.Test or RepositoryOwnershipArea.SharedInfrastructure;
}
