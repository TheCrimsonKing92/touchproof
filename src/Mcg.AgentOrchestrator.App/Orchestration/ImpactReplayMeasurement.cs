namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayMeasurement(int MaximumChangedSourceFiles,
    IReadOnlyDictionary<int, int> ChangedSourceFileCountDistribution, double NamedLaneSecondsSaved,
    double BalancedPoolSecondsSaved, double ObservedInfrastructureSeconds, double? ShareOfTestSecondsSkipped,
    int MissCount, int CompleteMissCheckCount, int IncompleteDurationCoverageCount)
{
    internal static ImpactReplayMeasurement Create(int maximum, IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        var seconds = attempts.Sum(a => a.ObservedInfrastructureSeconds);
        return new(maximum, attempts.GroupBy(a => a.ChangedSourceFileCount).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            attempts.Sum(a => a.EstimatedSerialSecondsSaved), attempts.Sum(a => a.PoolSecondsSaved), seconds,
            seconds > 0 ? attempts.Sum(a => a.NamedLaneObservedSecondsSaved + a.PoolSecondsSaved) / seconds : null,
            attempts.Sum(a => a.FailingClasses.Where(f => f.Lane is not null && f.WouldRun == false)
                .Select(f => f.ClassName).Distinct(StringComparer.Ordinal).Count()),
            attempts.Count(a => a.MissCheckComplete), attempts.Count(a => !a.DurationCoverageComplete));
    }
}
