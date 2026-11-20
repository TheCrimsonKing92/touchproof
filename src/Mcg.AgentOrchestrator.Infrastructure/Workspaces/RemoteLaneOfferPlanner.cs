namespace Mcg.AgentOrchestrator.Infrastructure;

// Duration hints affect placement only; they never authorize execution or determine a verdict.
internal static class RemoteLaneOfferPlanner
{
    internal static IReadOnlyList<RemoteLaneOfferDecision> Plan(IReadOnlyList<RemoteLaneOfferInputs> lanes,
        int concurrency, IReadOnlyList<int> freeSlots, Func<string, bool> isCandidate,
        double gateWallGrowthThreshold = RemoteLaneOfferPolicy.DefaultGateWallGrowthThreshold) =>
        Project(lanes, concurrency, freeSlots, isCandidate, gateWallGrowthThreshold).Decisions;

    internal static (IReadOnlyList<RemoteLaneOfferDecision> Decisions, double ProjectedLocalSeconds, double ProjectedMixedSeconds)
        Project(IReadOnlyList<RemoteLaneOfferInputs> lanes, int concurrency, IReadOnlyList<int> freeSlots,
            Func<string, bool> isCandidate, double gateWallGrowthThreshold)
    {
        var local = lanes.Select(input => RemoteLaneOfferPolicy.ResolveLocalSeconds(
            input.LocalMedianSeconds, input.LocalSamples, input.ManifestEstimateSeconds)).ToArray();
        var projectedLocal = RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(local, concurrency);
        var wallTimeLimit = projectedLocal * (1 + gateWallGrowthThreshold);
        var baseline = lanes.Select(input => RemoteLaneOfferPolicy.Decide(
            input with { ExpectedLocalFinishSeconds = projectedLocal })).ToArray();
        var remainingLocalFinish = projectedLocal;
        var remoteFinish = 0d;
        var admitted = new HashSet<int>();
        var plannedOffers = new int[freeSlots.Count];
        var decisions = new List<RemoteLaneOfferDecision>();
        var candidates = Enumerable.Range(0, lanes.Count).Where(index => isCandidate(lanes[index].LaneName) &&
                baseline[index].Reason is not ("short" or "remote-failing"))
            .OrderByDescending(index => local[index]).ThenBy(index => index).ToArray();
        foreach (var index in candidates)
        {
            var localFinishAfterOffer = RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(
                local.Where((_, lane) => lane != index && !admitted.Contains(lane)), concurrency);
            var finishWithoutCandidateRemote = Math.Max(localFinishAfterOffer, remoteFinish);
            var input = lanes[index] with { ExpectedLocalFinishSeconds = remainingLocalFinish };
            var solo = RemoteLaneOfferPolicy.Decide(input);
            var shared = RemoteLaneOfferPolicy.Decide(input, sharedExecutor: true);
            var executor = -1;
            var mixed = double.PositiveInfinity;
            var decision = solo;
            for (var slot = 0; slot < freeSlots.Count; slot++)
            {
                if (plannedOffers[slot] >= freeSlots[slot]) continue;
                var estimate = plannedOffers[slot] == 0 ? solo : shared;
                var candidateMixed = Math.Max(finishWithoutCandidateRemote, estimate.RemoteSeconds);
                if (executor >= 0 && candidateMixed >= mixed) continue;
                executor = slot;
                mixed = candidateMixed;
                decision = estimate;
            }

            // If all planned slots are spent, retain the shared estimate for capacity diagnostics.
            if (executor < 0)
            {
                decision = admitted.Count == 0 ? solo : shared;
                mixed = Math.Max(finishWithoutCandidateRemote, decision.RemoteSeconds);
            }

            if (mixed <= wallTimeLimit && executor >= 0)
            {
                decision = decision with { Offer = true, Reason = decision.Offer ? decision.Reason : "fits-growth" };
                admitted.Add(index);
                plannedOffers[executor]++;
                remainingLocalFinish = localFinishAfterOffer;
                remoteFinish = Math.Max(remoteFinish, decision.RemoteSeconds);
            }
            else if (mixed <= wallTimeLimit)
                decision = decision with { Offer = false, Reason = "capacity" };
            else
                decision = decision with { Offer = false, Reason =
                    baseline[index].Reason is "fits" or "fits-probe" or "fits-reprobe"
                        ? "remote-slower-after-offload" : "remote-slower" };
            decisions.Add(decision);
        }
        foreach (var index in Enumerable.Range(0, lanes.Count).Except(candidates))
            decisions.Add(baseline[index] with { Offer = false });
        return (decisions, projectedLocal, Math.Max(remainingLocalFinish, remoteFinish));
    }
}
