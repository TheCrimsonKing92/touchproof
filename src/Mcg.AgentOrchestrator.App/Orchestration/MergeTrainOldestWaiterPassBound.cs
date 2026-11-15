using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MergeTrainOldestWaiterPassBound
{
    private static readonly ConditionalWeakTable<ConductorBatchLoop, MergeTrainOldestWaiterPassBound> Instances = new();
    private Dictionary<GoalId, ConductorMergeTrainSkipReason> _lastSkips = [];
    private string? _lastDecline;

    internal static MergeTrainOldestWaiterPassBound For(ConductorBatchLoop loop) => Instances.GetValue(loop, static _ => new());

    internal ConductorMergeTrainSelection? Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlySet<string> suppressedPairs, IReadOnlySet<string> ineligibleGoalIds,
        IReadOnlySet<string> attributedMemberKeys, IReadOnlySet<string> implicatedMemberKeys,
        int tick, Action<string> progress)
    {
        var skips = new Dictionary<GoalId, ConductorMergeTrainSkipReason>();
        var selection = ConductorMergeTrainSelector.Select(candidates, suppressedPairs, ineligibleGoalIds,
            attributedMemberKeys, implicatedMemberKeys, (goal, reason) => skips[goal] = reason);
        RecordSkips(skips, tick, progress);
        return selection;
    }

    internal static bool ShouldDecline(Goal? waiter, IReadOnlyList<GoalId> members,
        bool waiterHandled, bool waiterEligible, LiveAcceptanceCensus census,
        Func<Goal, LiveAcceptanceCensus, SoloAcceptanceAdmission> soloAdmission)
    {
        // Verifying goals use the transient-retry priority path, not the ready-waiter rule.
        if (waiter is null || waiter.Status != GoalStatus.Verified ||
            members.Contains(waiter.Id) || waiterHandled || !waiterEligible) return false;
        try
        {
            // A train may coexist with the waiter when another acceptance unit remains.
            return soloAdmission(waiter, census).Kind == SoloAcceptanceAdmissionKind.Admissible &&
                soloAdmission(waiter, census with { Occupants = [.. census.Occupants, "proposed-train"] }).Kind !=
                    SoloAcceptanceAdmissionKind.Admissible;
        }
        catch
        {
            // Match the solo-priority probes: an unavailable candidate cannot reserve a unit.
            return false;
        }
    }

    internal bool AllowsTrain(Goal? waiter, IReadOnlyList<GoalId> members,
        bool waiterHandled, bool waiterEligible, LiveAcceptanceCensus census,
        Func<Goal, LiveAcceptanceCensus, SoloAcceptanceAdmission> soloAdmission,
        int tick, Action<string> progress)
    {
        var declined = ShouldDecline(waiter, members, waiterHandled, waiterEligible, census, soloAdmission);
        RecordDecline(declined ? waiter!.Id : null, members, tick, progress);
        return !declined;
    }

    internal void RecordSkips(IReadOnlyDictionary<GoalId, ConductorMergeTrainSkipReason> skips,
        int tick, Action<string> progress)
    {
        foreach (var (goal, reason) in skips)
            if (!_lastSkips.TryGetValue(goal, out var previous) || previous != reason)
                progress($"ADMISSION tick={tick} result=train-skipped reason={reason} goal={goal.Value[..8]}");
        _lastSkips = skips.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    internal void RecordDecline(GoalId? waiter, IReadOnlyList<GoalId> members, int tick, Action<string> progress)
    {
        var key = waiter is null ? null : $"{waiter.Value}:{string.Join(',', members.Select(id => id.Value))}";
        if (key is not null && key != _lastDecline)
            progress($"ADMISSION tick={tick} result=train-declined reason=oldest-waiter-solo-admissible " +
                $"oldest={waiter!.Value[..8]} members={string.Join(',', members.Select(id => id.Value[..8]))}");
        _lastDecline = key;
    }
}
