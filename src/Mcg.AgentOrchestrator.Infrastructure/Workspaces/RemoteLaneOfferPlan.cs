using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the cached placement decisions and gate wall-time projections for one coordinator.
internal sealed class RemoteLaneOfferPlan(IReadOnlyDictionary<string, RemoteLaneOfferInputs> inputs,
    Func<IReadOnlyList<int>> takeFreeSlots, Func<string, bool> isCandidate, double gateWallGrowthThreshold)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RemoteLaneOfferDecision> _decisions = new(StringComparer.Ordinal);
    private (IReadOnlyDictionary<string, RemoteLaneOfferDecision> Decisions, double LocalSeconds, double MixedSeconds)? _planned;

    internal RemoteLaneOfferPlan(IReadOnlyDictionary<string, RemoteLaneOfferInputs> inputs,
        RemoteLaneExecutorConfiguration configuration, string worktreePath, Func<string, bool> isCandidate)
        : this(inputs, () => RemoteExecutorOccupancyCensus.TakeGateFreeSlots(
                AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
                configuration.Executors.Select(entry => (entry.Id, entry.Slots))),
            isCandidate, configuration.GateWallGrowthThreshold) { }

    internal RemoteLaneOfferPlan(IReadOnlyDictionary<string, RemoteLaneOfferInputs> inputs,
        IReadOnlyList<int> freeSlots, Func<string, bool> isCandidate)
        : this(inputs, () => freeSlots, isCandidate, RemoteLaneOfferPolicy.DefaultGateWallGrowthThreshold) { }

    internal bool ShouldOffer(string lane, int concurrency, TimeSpan checkTimeout, Action<string> report,
        GateLaneOfferOutcomeCollector? offerOutcomes)
    {
        var decision = Decide(lane, concurrency, checkTimeout, report);
        if (!decision.Offer)
            offerOutcomes?.Record(lane, "keep-local", decision.Reason);
        return decision.Offer;
    }

    internal RemoteLaneOfferDecision Decide(string lane, int concurrency, TimeSpan checkTimeout, Action<string> report)
    {
        lock (_sync)
        {
            if (_decisions.TryGetValue(lane, out var existing)) return existing;
            var plan = _planned ??= Build(concurrency);
            var decision = plan.Decisions[lane];
            _decisions.Add(lane, decision);
            report(string.Create(CultureInfo.InvariantCulture,
                $"REMOTE_LANE_POLICY lane=\"{lane}\" decision={(decision.Offer ? "offer" : "keep-local")} reason={decision.Reason} L={decision.LocalSeconds:0.###} Ln={decision.Inputs.LocalSamples} R={decision.RemoteSeconds:0.###} Rn={decision.Inputs.RemoteSamples} F={decision.Inputs.ExpectedLocalFinishSeconds:0.###}") + RemoteLaneOfferPolicy.FailureFields(decision) +
                string.Create(CultureInfo.InvariantCulture,
                    $" DeadlineSeconds={RemoteLaneDeadline.Resolve(decision, checkTimeout).TotalSeconds} CheckTimeoutSeconds={checkTimeout.TotalSeconds} projectedLocalSeconds={plan.LocalSeconds} projectedMixedSeconds={plan.MixedSeconds}"));
            return decision;
        }
    }

    private (IReadOnlyDictionary<string, RemoteLaneOfferDecision> Decisions, double LocalSeconds, double MixedSeconds)
        Build(int concurrency)
    {
        var projection = RemoteLaneOfferPlanner.Project(inputs.Values.ToArray(), concurrency, takeFreeSlots(), isCandidate,
            gateWallGrowthThreshold);
        return (projection.Decisions.ToDictionary(decision => decision.Inputs.LaneName, StringComparer.Ordinal),
            projection.ProjectedLocalSeconds, projection.ProjectedMixedSeconds);
    }

    internal TimeSpan Deadline(string lane, TimeSpan checkTimeout)
    {
        lock (_sync)
            return RemoteLaneDeadline.Resolve(_decisions.GetValueOrDefault(lane), checkTimeout);
    }
}
