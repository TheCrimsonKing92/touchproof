namespace Mcg.AgentOrchestrator.Core;

// Matches the standard deferred-report requirement when no literal names required classes.
internal static class UnnamedDeveloperReportEvidence
{
    internal static bool TryMatch(string criterion, TaskSpec developer, out string? tests, out string? skipReason)
    {
        tests = null;
        skipReason = null;
        var found = false;
        for (var offset = 0; offset < criterion.Length;)
        {
            var start = criterion.IndexOf('`', offset);
            if (start < 0) break;
            var end = criterion.IndexOf('`', start + 1);
            if (end < 0) break;
            var field = DeveloperReportEvidence.FindRequiredField(criterion[start..(end + 1)]);
            if (field is not null)
            {
                // A later named literal must not be bypassed by an earlier empty one.
                if (DeveloperDeferredTestClassNames.Parse(field).Count > 0) return false;
                found = true;
            }
            offset = end + 1;
        }
        if (!found) return false;
        if (!DeveloperReportEvidence.TryReadReport(developer, out var reported) ||
            DeveloperDeferredTestClassNames.Parse(reported).Count == 0)
        {
            skipReason = "developer-report-missing";
            return false;
        }
        tests = reported;
        return true;
    }

    internal static bool IsNotMet(TaskVerificationRecord verification, int criterionIndex) =>
        WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out _) &&
        verdicts.Any(item => item.CriterionIndex == criterionIndex && item.Verdict == "not-met");
}
