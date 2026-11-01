namespace Mcg.AgentOrchestrator.Infrastructure;

// Pure longest-processing-time packing. Unknown units follow known units so adding an
// unknown class cannot rearrange the measured layout. A shared collection is indivisible.
internal static class AcceptanceBalancedLanePacker
{
    internal static AcceptanceBalancedLaneLayout Pack(IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyDictionary<string, double> medians, AcceptanceBalancedLanePoolSettings settings)
    {
        settings.Validate([]);
        var known = medians.Where(pair => double.IsFinite(pair.Value) && pair.Value >= 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var estimate = known.Count == 0 ? settings.DefaultClassSecondsWhenNoHistory : Median(known.Values);
        var units = classes.DistinctBy(item => item.FullName)
            .GroupBy(item => item.Collection is null ? "class:" + item.FullName : "collection:" + item.Collection,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Members = group.Select(item => item.FullName).Order(StringComparer.Ordinal).ToArray(),
                Known = group.All(item => known.ContainsKey(item.FullName)),
                Seconds = group.Sum(item => known.GetValueOrDefault(item.FullName, estimate))
            }).ToArray();
        var members = Enumerable.Range(0, Math.Min(settings.LaneCount, units.Length)).Select(_ => new List<string>()).ToArray();
        var totals = new double[members.Length];
        foreach (var unit in units.OrderByDescending(item => item.Known)
                     .ThenByDescending(item => item.Known ? item.Seconds : 0)
                     .ThenBy(item => item.Members[0], StringComparer.Ordinal))
        {
            var index = Enumerable.Range(0, members.Length).OrderBy(index => totals[index]).ThenBy(index => index).First();
            members[index].AddRange(unit.Members);
            totals[index] += unit.Seconds;
        }
        return new(settings.HistoryWindow, estimate, members.Select((list, index) =>
            new AcceptanceBalancedLanePrediction($"{settings.LaneNamePrefix} {index + 1}", totals[index], list.ToArray())).ToArray(),
            settings.ResidualLaneName, []);
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("A median requires observations.", nameof(values));
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? sorted[middle - 1] / 2 + sorted[middle] / 2 : sorted[middle];
    }
}
