using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Diagnostic only: recorded selections, lane skips and savings remain authoritative.
internal static class ImpactReplayShippingFollowMeasurement
{
    internal static ImpactReplayAttemptRecord Apply(ImpactReplayAttemptRecord attempt, string checkout,
        IReadOnlyList<string> productionSources, ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, ImpactReplaySourceGuardReport? guards,
        IReadOnlyList<string> additionalClasses, IReadOnlyList<string> testProjectDirectories,
        IReadOnlySet<string> recordedFullNames, IReadOnlyList<AcceptanceTestLane> lanes)
    {
        if (attempt.SelectionOutcome != "Resolved")
            return Unavailable(attempt, $"Recorded selection {attempt.SelectionOutcome}: {attempt.SelectionReason}");
        try
        {
            var associations = new HashSet<(string Type, string Class)>();
            var selection = productionSources.Count == 0
                ? ReverseDependencyTestSelection.Resolved([])
                : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: true,
                    propagateShippingSourceReach: true,
                    shippingReachDiagnostics: (type, name) => associations.Add((type, name)));
            selection = ImpactReplaySelectionAugmentation.Apply(selection, attempt.ChangedFiles, sources,
                classes, guards, out _, additionalClasses, testProjectDirectories);
            if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved)
                return Unavailable(attempt, selection.Reason ?? $"Shipping-follow selection {selection.Outcome}.");

            var selected = new HashSet<string>(StringComparer.Ordinal);
            var mapped = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var name in selection.TestClassNames)
            {
                var matches = classes.Where(item => name.Contains('.') || name.Contains('+')
                    ? item.FullName == name : item.FullName.Split('.', '+').Last() == name)
                    .Select(item => item.FullName).ToArray();
                if (matches.Length == 0)
                    return Unavailable(attempt, $"Shipping-follow selection has an unmapped test class: {name}");
                mapped[name] = matches;
                selected.UnionWith(matches);
            }

            var added = selected.Except(recordedFullNames, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var addedSet = added.ToHashSet(StringComparer.Ordinal);
            var addedLanes = selected.SelectMany(name => AcceptanceLaneMembership.LanesIncluding(lanes, name))
                .Select(lane => lane.Name).Except(attempt.WouldRunLanes, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var attributions = associations.GroupBy(item => item.Type, StringComparer.Ordinal)
                .Select(group => new ImpactReplayShippingFollowAttribution(group.Key,
                    group.SelectMany(item => mapped.GetValueOrDefault(item.Class) ?? [])
                        .Where(addedSet.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
                .Where(item => item.AddedClasses.Count > 0)
                .OrderBy(item => item.ShippingType, StringComparer.Ordinal).ToArray();

            var retained = ImpactReplayClassLevelSavings.RetainedClasses(recordedFullNames, guards?.Classes ?? [],
                lanes, attempt.FloorLanes, classes);
            var complement = classes.Select(item => item.FullName)
                .Where(name => !addedSet.Contains(name) || retained.Contains(name)).ToHashSet(StringComparer.Ordinal);
            var durations = added.Length == 0 ? null : ImpactReplayClassLevelSavings.Measure(attempt.LaneOutcomes,
                attempt.BalancedLaneOutcomes ?? [], attempt.FloorLanes, complement, classes,
                resolved: true, abandoned: false, durationComplete: true);
            var seconds = added.Length == 0 ? 0 : durations!.SecondsSaved;
            var remaining = attempt.ClassLevelSecondsSaved is > 0 && seconds.HasValue
                ? 100 * (attempt.ClassLevelSecondsSaved.Value - seconds.Value) / attempt.ClassLevelSecondsSaved.Value
                : (double?)null;
            return attempt with
            {
                ShippingFollowAddedClasses = added,
                ShippingFollowAddedLanes = addedLanes,
                ShippingFollowAttributions = attributions,
                ShippingFollowAddedSeconds = seconds,
                ShippingFollowRemainingSavingPercent = remaining,
                ShippingFollowClassesUnavailableReason = null,
                ShippingFollowSecondsUnavailableReason = seconds.HasValue ? null : durations!.UnavailableReason,
                ShippingFollowRemainingSavingUnavailableReason = remaining.HasValue ? null :
                    !seconds.HasValue ? durations!.UnavailableReason :
                    attempt.ClassLevelUnavailableReason ?? "Recorded class-level saving is unavailable or nonpositive."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(attempt, $"Shipping-follow selection unavailable: {ex.Message}");
        }
    }

    private static ImpactReplayAttemptRecord Unavailable(ImpactReplayAttemptRecord attempt, string reason) =>
        attempt with
        {
            ShippingFollowAddedClasses = null,
            ShippingFollowAddedLanes = null,
            ShippingFollowAttributions = null,
            ShippingFollowAddedSeconds = null,
            ShippingFollowRemainingSavingPercent = null,
            ShippingFollowClassesUnavailableReason = reason,
            ShippingFollowSecondsUnavailableReason = reason,
            ShippingFollowRemainingSavingUnavailableReason = reason
        };
}
