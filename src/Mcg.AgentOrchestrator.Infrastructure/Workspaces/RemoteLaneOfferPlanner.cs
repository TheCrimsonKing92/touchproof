namespace Mcg.AgentOrchestrator.Infrastructure;

// Duration hints affect placement only; they never authorize execution or determine a verdict.
internal static class RemoteLaneOfferPlanner
{
    internal static IReadOnlyList<RemoteLaneOfferDecision> Plan(IReadOnlyList<RemoteLaneOfferInputs> lanes,
        int concurrency, IReadOnlyList<int> executorSlots, Func<string, bool> isCandidate)
    {
        var local = lanes.Select(input => RemoteLaneOfferPolicy.ResolveLocalSeconds(
            input.LocalMedianSeconds, input.LocalSamples, input.ManifestEstimateSeconds)).ToArray();
        var initialFinish = RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(local, concurrency);
        var baseline = lanes.Select(input => RemoteLaneOfferPolicy.Decide(
            input with { ExpectedLocalFinishSeconds = initialFinish })).ToArray();
        var admitted = new HashSet<int>();
        var occupied = new int[executorSlots.Count];
        var decisions = new List<RemoteLaneOfferDecision>();
        var candidates = Enumerable.Range(0, lanes.Count).Where(index => isCandidate(lanes[index].LaneName) &&
                baseline[index].Reason is not ("short" or "remote-failing"))
            .OrderBy(index => baseline[index].RemoteSeconds / Math.Max(double.Epsilon, local[index]))
            .ThenByDescending(index => local[index]).ThenBy(index => index).ToArray();
        foreach (var index in candidates)
        {
            var finish = RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(
                local.Where((_, lane) => !admitted.Contains(lane)), concurrency);
            var executor = Enumerable.Range(0, executorSlots.Count)
                .Where(slot => occupied[slot] < executorSlots[slot])
                .OrderBy(slot => occupied[slot]).ThenBy(slot => slot).DefaultIfEmpty(-1).First();
            var estimateExecutor = executor >= 0 ? executor : Enumerable.Range(0, executorSlots.Count)
                .Where(slot => executorSlots[slot] > 0).OrderBy(slot => occupied[slot]).DefaultIfEmpty(-1).First();
            var decision = RemoteLaneOfferPolicy.Decide(lanes[index] with { ExpectedLocalFinishSeconds = finish },
                sharedExecutor: estimateExecutor >= 0 && occupied[estimateExecutor] > 0);
            // Measured slack cannot extend the gate tail. Unmeasured exploration keeps its existing bounds.
            var fits = decision.Reason is "fits" or "fits-probe" or "fits-reprobe";
            if (fits && executor >= 0)
            {
                admitted.Add(index);
                occupied[executor]++;
            }
            else if (fits)
                decision = decision with { Offer = false, Reason = "capacity" };
            else if (decision.Reason is not ("short" or "remote-failing"))
                decision = decision with { Offer = false, Reason =
                    baseline[index].Reason is "fits" or "fits-probe" or "fits-reprobe"
                        ? "remote-slower-after-offload" : "remote-slower" };
            decisions.Add(decision);
        }
        foreach (var index in Enumerable.Range(0, lanes.Count).Except(candidates))
            decisions.Add(baseline[index] with { Offer = false });
        return decisions;
    }
}
