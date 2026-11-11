using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Diagnostic only: never substitutes an unbounded selection for the recorded execution decision.
internal static class ImpactReplayUnboundedSelectionMeasurement
{
    internal static bool IsBound(string? kind) => kind is
        nameof(ReverseDependencyDegradationKind.SelectedTestClassBound) or
        nameof(ReverseDependencyDegradationKind.ChangedSourceFileCount) or
        nameof(ReverseDependencyDegradationKind.FrontierSymbolBound);

    internal static ImpactReplayAttemptRecord Apply(ImpactReplayAttemptRecord attempt, string checkout,
        IReadOnlyList<string> productionSources, ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, ImpactReplaySourceGuardReport? guards,
        IReadOnlyList<string> additionalClasses)
    {
        if (attempt.SelectionOutcome != "Abandoned" || !IsBound(attempt.DegradationKind)) return attempt;
        try
        {
            var selection = productionSources.Count == 0
                ? ReverseDependencyTestSelection.Resolved([], headroom: new(null,
                    ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles, 0, int.MaxValue, null, int.MaxValue))
                : ReverseDependencyTestImpactReader.Read(checkout, productionSources, bypassCache: true,
                    maximumSelectedTestClasses: int.MaxValue, maximumFrontierSymbols: int.MaxValue,
                    maximumChangedSourceFiles: int.MaxValue);
            selection = ImpactReplaySelectionAugmentation.Apply(selection, attempt.ChangedFiles, sources,
                classes, guards, out _, additionalClasses);
            if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved)
                return Unavailable(attempt, selection.Reason ?? $"Unbounded selection {selection.Outcome}.");

            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in selection.TestClassNames)
            {
                var matches = classes.Where(item => name.Contains('.') || name.Contains('+')
                    ? item.FullName == name : item.FullName.Split('.', '+').Last() == name).ToArray();
                if (matches.Length == 0)
                    return Unavailable(attempt, $"Unbounded selection has an unmapped test class: {name}");
                selected.UnionWith(matches.Select(item => item.FullName));
            }

            // Measure's saved seconds for the complement are exactly the selected set's seconds.
            // Reuse its floor exclusion, receipt deduplication and all-or-nothing TRX coverage.
            var complement = classes.Select(item => item.FullName).Where(name => !selected.Contains(name))
                .ToHashSet(StringComparer.Ordinal);
            var durations = ImpactReplayClassLevelSavings.Measure(attempt.LaneOutcomes,
                attempt.BalancedLaneOutcomes ?? [], attempt.FloorLanes, complement, classes,
                resolved: true, abandoned: false, durationComplete: true);
            var share = durations.NonFloorTestSeconds is > 0 && durations.SecondsSaved is { } seconds
                ? seconds / durations.NonFloorTestSeconds.Value : (double?)null;
            return attempt with
            {
                UnboundedSelectedClassCount = selected.Count,
                UnboundedNonFloorTestSecondsShare = share,
                UnboundedClassCountUnavailableReason = null,
                UnboundedDurationShareUnavailableReason = share.HasValue ? null :
                    durations.UnavailableReason ?? "Non-floor test seconds are zero."
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(attempt, $"Unbounded selection unavailable: {ex.Message}");
        }
    }

    private static ImpactReplayAttemptRecord Unavailable(ImpactReplayAttemptRecord attempt, string reason) =>
        attempt with
        {
            UnboundedSelectedClassCount = null,
            UnboundedNonFloorTestSecondsShare = null,
            UnboundedClassCountUnavailableReason = reason,
            UnboundedDurationShareUnavailableReason = reason
        };
}
