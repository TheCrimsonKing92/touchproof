using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Offline counterfactual cost only. Selection and the existing lane measurements remain authoritative.
internal static class ImpactReplayClassLevelSavings
{
    internal static IReadOnlySet<string> RetainedClasses(IEnumerable<string> selectedFullNames,
        IEnumerable<string> guardClasses, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<string> floorLanes, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var retained = new HashSet<string>(selectedFullNames, StringComparer.Ordinal);
        retained.UnionWith(guardClasses);
        retained.UnionWith(classes.Where(c => AcceptanceLaneMembership.LanesIncluding(lanes, c.FullName)
            .Any(lane => floorLanes.Contains(lane.Name, StringComparer.Ordinal))).Select(c => c.FullName));
        return retained;
    }

    internal static ImpactReplayClassLevelEstimate Measure(IReadOnlyList<ImpactReplayLaneOutcome> laneOutcomes,
        IReadOnlyList<ImpactReplayLaneOutcome> balancedLaneOutcomes, IReadOnlyList<string> floorLanes,
        IReadOnlySet<string> retained, IReadOnlyList<AcceptanceTestClassDescriptor> classes,
        bool resolved, bool abandoned, bool durationComplete)
    {
        const string incomplete = "incomplete TRX duration or class evidence";
        var lanes = laneOutcomes.Concat(balancedLaneOutcomes).ToArray();
        if (!durationComplete || lanes.Any(lane => !floorLanes.Contains(lane.Lane, StringComparer.Ordinal) && lane.TrxPaths.Count == 0))
            return new(null, null, incomplete);

        try
        {
            var paths = lanes.SelectMany(lane => lane.TrxPaths.Select(path =>
                    (Path: Path.GetFullPath(path), Floor: floorLanes.Contains(lane.Lane, StringComparer.Ordinal))))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            // Identical receipt copies are the same executions, even under different filenames.
            var artifacts = paths.Select(group =>
            {
                using var stream = File.OpenRead(group.Key);
                return (Path: group.Key, Floor: group.Any(item => item.Floor),
                    Hash: Convert.ToHexString(SHA256.HashData(stream)));
            }).GroupBy(item => item.Hash, StringComparer.Ordinal);
            var results = new List<(string? ClassName, double? Seconds)>();
            foreach (var artifact in artifacts)
            {
                if (artifact.Any(item => item.Floor)) continue;
                var read = ImpactReplayTrxDurations.Read(artifact.First().Path, classes);
                if (!read.Complete || read.Results.Any(result => string.IsNullOrWhiteSpace(result.ClassName)))
                    return new(null, null, incomplete);
                results.AddRange(read.Results);
            }
            var baseline = results.Sum(result => result.Seconds!.Value);
            if (abandoned) return new(0, baseline, null);
            if (!resolved) return new(null, baseline, "selection unresolved");
            var scanned = classes.Select(c => c.FullName).ToHashSet(StringComparer.Ordinal);
            return new(results.Where(result => scanned.Contains(result.ClassName!) && !retained.Contains(result.ClassName!))
                .Sum(result => result.Seconds!.Value), baseline, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(null, null, incomplete);
        }
    }
}
