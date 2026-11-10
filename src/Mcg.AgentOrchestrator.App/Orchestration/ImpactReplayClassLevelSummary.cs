namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayClassLevelSummary(double TotalSecondsSaved, double? MedianSecondsSaved,
    double NonFloorTestSeconds, double? ShareOfNonFloorTestSeconds, int AttemptsMeasured,
    int AttemptsExcludedForIncompleteTrx, int AttemptsWithUnresolvedSelection)
{
    internal static ImpactReplayClassLevelSummary Create(IReadOnlyList<ImpactReplayAttemptRecord> attempts)
    {
        var measured = attempts.Where(a => a.ClassLevelNonFloorTestSeconds is not null).ToArray();
        var savings = measured.Where(a => a.ClassLevelSecondsSaved is not null)
            .Select(a => a.ClassLevelSecondsSaved!.Value).Order().ToArray();
        var total = savings.Sum();
        var baseline = measured.Sum(a => a.ClassLevelNonFloorTestSeconds!.Value);
        double? median = savings.Length == 0 ? null :
            (savings[(savings.Length - 1) / 2] + savings[savings.Length / 2]) / 2;
        return new(total, median, baseline, baseline > 0 ? total / baseline : null, measured.Length,
            attempts.Count - measured.Length, measured.Count(a => a.ClassLevelSecondsSaved is null));
    }
}
