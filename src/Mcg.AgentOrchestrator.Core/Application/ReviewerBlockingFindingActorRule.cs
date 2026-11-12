namespace Mcg.AgentOrchestrator.Core;

internal static class ReviewerBlockingFindingActorRule
{
    internal const string RuleText =
        "A finding may be blocking only if a Developer can change code or tests to resolve it, or a Tester evidence_request naming test classes can produce the evidence. Evidence that only the acceptance gate produces (full-gate receipts, gate-owned criteria execution) is recorded as not-verifiable for its criterion and is never a blocking finding.";

    internal const string RequirementLine = "- " + RuleText;

    internal const string RepairMessage =
        "Reviewer verdict needs-work is not actionable: every open blocking finding is a test-evidence finding without evidence_request. Rule: " +
        RuleText +
        " Add evidence_request naming test classes to each such finding, or record the criterion as not-verifiable and remove the blocking finding.";

    internal static bool IsUnactionableNeedsWork(string verdict, IReadOnlyList<ReviewFinding> findings)
    {
        if (verdict != "needs-work")
            return false;

        var hasOpenBlockingFinding = false;
        foreach (var finding in findings)
        {
            if (finding.State != ReviewFindingState.Open || finding.Severity != FindingSeverity.Blocking)
                continue;

            hasOpenBlockingFinding = true;
            if (finding.Category != FindingCategory.TestEvidence || finding.EvidenceRequest is not null)
                return false;
        }

        return hasOpenBlockingFinding;
    }
}
