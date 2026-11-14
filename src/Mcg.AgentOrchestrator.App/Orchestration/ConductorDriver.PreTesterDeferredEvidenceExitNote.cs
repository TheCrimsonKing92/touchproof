using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum PreTesterDeferredEvidenceExit
{
    PriorOutcomeNotStarted,
    NoResolvedClasses,
    NoNormalizedClasses,
    RefreshUnusableContinues
}

internal static class PreTesterDeferredEvidenceExitNote
{
    public static string FormatSkipped(
        string testsField,
        PreTesterDeferredEvidenceExit exit,
        string? priorOutcome,
        IReadOnlyList<string> notRun)
    {
        var slug = Slug(exit);
        var parsed = DeveloperDeferredTestClassNames.Parse(testsField);
        static string FormatNames(IEnumerable<string> names) =>
            string.Join(",", names.DefaultIfEmpty("<none>"));
        return $"pre-tester-deferred-evidence skipped: exit={slug} " +
            $"prior={priorOutcome ?? "<none>"} parsed={FormatNames(parsed)} not_run={FormatNames(notRun)}";
    }

    public static string FormatContinuation(string candidateSha, string supersededReceiptId, string freshReceiptId)
    {
        var slug = Slug(PreTesterDeferredEvidenceExit.RefreshUnusableContinues);
        return $"pre-tester-deferred-evidence continued: exit={slug} " +
            PreTesterEvidenceRefreshRule.DescribeContinueAfterUnusableRefresh(candidateSha, supersededReceiptId, freshReceiptId);
    }

    private static string Slug(PreTesterDeferredEvidenceExit exit) => exit switch
    {
        PreTesterDeferredEvidenceExit.PriorOutcomeNotStarted => "prior-outcome-not-started",
        PreTesterDeferredEvidenceExit.NoResolvedClasses => "no-resolved-classes",
        PreTesterDeferredEvidenceExit.NoNormalizedClasses => "no-normalized-classes",
        PreTesterDeferredEvidenceExit.RefreshUnusableContinues => "refresh-unusable-continues",
        _ => throw new InvalidDataException("Unknown pre-Tester deferred-evidence exit.")
    };
}
