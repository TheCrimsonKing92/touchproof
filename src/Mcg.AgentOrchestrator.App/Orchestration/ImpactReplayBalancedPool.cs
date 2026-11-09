using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplayBalancedPool
{
    internal static IReadOnlyList<string> LaneNames(AcceptanceBalancedLanePoolSettings? pool) => pool is null ? [] :
        Enumerable.Range(1, pool.LaneCount).Select(i => $"{pool.LaneNamePrefix} {i}").Append(pool.ResidualLaneName).ToArray();

    internal static (IReadOnlyList<ImpactReplayBalancedClass> Classes, double SecondsSaved,
        double ObservedSeconds, bool Complete) Read(IReadOnlyList<ImpactReplayLaneOutcome> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlySet<string> selected, bool resolved)
    {
        var records = new List<ImpactReplayBalancedClass>();
        var complete = true;
        double observed = 0, saved = 0;
        foreach (var lane in lanes)
        {
            if (lane.TrxPaths.Count == 0) complete = false;
            foreach (var path in lane.TrxPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var read = ImpactReplayTrxDurations.Read(path, classes);
                complete &= read.Complete && read.Results.All(r => r.ClassName is not null);
                observed += read.Results.Sum(r => r.Seconds ?? 0);
                foreach (var group in read.Results.Where(r => r.ClassName is not null).GroupBy(r => r.ClassName!))
                {
                    var scanned = classes.Any(c => c.FullName == group.Key);
                    var wouldRun = !resolved || !scanned || selected.Contains(group.Key);
                    // Keep readable cost even if another result in this association lacks a duration.
                    var knownSeconds = group.Sum(r => r.Seconds ?? 0);
                    double? seconds = group.All(r => r.Seconds is not null) ? knownSeconds : null;
                    if (!wouldRun) saved += knownSeconds;
                    records.Add(new(group.Key, lane.Lane, path, seconds, scanned, wouldRun));
                }
            }
        }
        return (records, saved, observed, complete);
    }
}
