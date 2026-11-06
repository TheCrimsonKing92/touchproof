namespace Mcg.AgentOrchestrator.Infrastructure;

// Deterministic minimax wall-model packing. Unknown units follow known units so adding an
// unknown class cannot rearrange the measured layout. A shared collection is indivisible.
internal static class AcceptanceBalancedLanePacker
{
    internal static AcceptanceBalancedLaneLayout Pack(IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        IReadOnlyDictionary<string, double> medians, AcceptanceBalancedLanePoolSettings settings,
        IReadOnlyDictionary<string, AcceptanceLaneWallModel.Input>? modelInputs = null)
    {
        settings.Validate([]);
        var known = medians.Where(pair => double.IsFinite(pair.Value) && pair.Value >= 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var estimate = known.Count == 0 ? settings.DefaultClassSecondsWhenNoHistory : Median(known.Values);
        // Never derive capacity from the replay host. Older history conservatively uses one slot.
        var slots = modelInputs?.Values.Where(input => input.SlotCount is > 0)
            .Select(input => input.SlotCount!.Value).DefaultIfEmpty(1).Max() ?? 1;
        var model = new AcceptanceLaneWallModel(slots);
        var units = classes.DistinctBy(item => item.FullName)
            .OrderBy(item => item.FullName, StringComparer.Ordinal)
            .GroupBy(item => item.Collection is null ? "class:" + item.FullName : "collection:" + item.Collection,
                StringComparer.Ordinal)
            .Select(group => new
            {
                Members = group.Select(item => item.FullName).Order(StringComparer.Ordinal).ToArray(),
                Known = group.All(item => known.ContainsKey(item.FullName)),
                Seconds = group.Sum(item => known.GetValueOrDefault(item.FullName, estimate)),
                Wall = new AcceptanceLaneWallModel.Unit(
                    group.Sum(item => known.GetValueOrDefault(item.FullName, estimate)),
                    group.Sum(item => modelInputs?.GetValueOrDefault(item.FullName)?.SlotHeldSeconds ??
                        known.GetValueOrDefault(item.FullName, estimate)), group.First().Collection is not null)
            }).ToArray();
        var members = Enumerable.Range(0, Math.Min(settings.LaneCount, units.Length)).Select(_ => new List<string>()).ToArray();
        var totals = new double[members.Length];
        var walls = members.Select(_ => new List<AcceptanceLaneWallModel.Unit>()).ToArray();
        var wallPredictions = new double[members.Length];
        var knownLaneCount = Math.Min(settings.LaneCount, units.Count(unit => unit.Known));
        foreach (var unit in units.OrderByDescending(item => item.Known)
                     .ThenByDescending(item => item.Known ? model.Predict([item.Wall]) : 0)
                     .ThenBy(item => item.Members[0], StringComparer.Ordinal))
        {
            var currentMax = wallPredictions.DefaultIfEmpty(0).Max();
            var index = unit.Known
                ? Enumerable.Range(0, knownLaneCount)
                    .OrderBy(candidate => Math.Max(currentMax, model.Predict(walls[candidate].Append(unit.Wall))))
                    .ThenBy(candidate => totals[candidate]).ThenBy(candidate => candidate).First()
                : Enumerable.Range(0, members.Length).OrderBy(lane => totals[lane]).ThenBy(lane => lane).First();
            members[index].AddRange(unit.Members);
            totals[index] += unit.Seconds;
            walls[index].Add(unit.Wall);
            wallPredictions[index] = model.Predict(walls[index]);
        }
        return new(settings.HistoryWindow, estimate, members.Select((list, index) =>
            new AcceptanceBalancedLanePrediction($"{settings.LaneNamePrefix} {index + 1}", totals[index], list.ToArray())
                { PredictedWallSeconds = model.Predict(walls[index]) }).ToArray(),
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
