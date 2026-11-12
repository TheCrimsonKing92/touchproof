namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayMeasurement(
    IReadOnlyDictionary<int, int> ChangedSourceFileCountDistribution, double NamedLaneSecondsSaved,
    double BalancedPoolSecondsSaved, double ObservedInfrastructureSeconds, double? ShareOfTestSecondsSkipped,
    int MissCount, int CompleteMissCheckCount, int IncompleteDurationCoverageCount,
    ImpactReplayClassLevelSummary? ClassLevel = null, int FlakeExcludedCount = 0)
{
    public string Interpretation =>
        "Miss count includes only true misses classified by IsMiss. Miss and flake-excluded counts each count distinct class names " +
        "(ordinal comparison) within each attempt, summed across attempts. The categories are counted independently: a class " +
        "with both miss and flake rows appears in both counts, so these are not an additive partition of failure rows. " +
        "Unknown or unresolved evidence remains in the attempt diagnostics; consult incomplete miss checks and unresolved failures. " +
        "Zero true misses does not establish selection safety. Historical summaries may include skipped flaky failures and are " +
        "not directly comparable to corrected true-miss totals without reconciliation.";

    internal static ImpactReplayMeasurement Create(IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        var seconds = attempts.Sum(a => a.ObservedInfrastructureSeconds);
        return new(attempts.GroupBy(a => a.ChangedSourceFileCount).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            attempts.Sum(a => a.EstimatedSerialSecondsSaved), attempts.Sum(a => a.PoolSecondsSaved), seconds,
            seconds > 0 ? attempts.Sum(a => a.NamedLaneObservedSecondsSaved + a.PoolSecondsSaved) / seconds : null,
            attempts.Sum(a => a.FailingClasses.Where(f => f.IsMiss)
                .Select(f => f.ClassName).Distinct(StringComparer.Ordinal).Count()),
            attempts.Count(a => a.MissCheckComplete), attempts.Count(a => !a.DurationCoverageComplete),
            ImpactReplayClassLevelSummary.Create(attempts),
            attempts.Sum(a => a.FailingClasses.Where(f => f.IsFlakyFailureInSkippedLane)
                .Select(f => f.ClassName).Distinct(StringComparer.Ordinal).Count()));
    }
}
