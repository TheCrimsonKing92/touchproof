using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RemoteLaneNameDurationHistory(
    IReadOnlyDictionary<string, RemoteExecutorLaneSeconds> byLaneName)
{
    internal RemoteExecutorLaneSeconds? Select(
        IReadOnlyDictionary<(string Lane, string Filter), RemoteExecutorLaneSeconds> exact,
        (string Lane, string Filter) key)
    {
        if (exact.TryGetValue(key, out var matching) && matching.Samples >= RemoteLaneOfferPolicy.MinimumSamples)
            return matching;

        return byLaneName.TryGetValue(key.Lane, out var pooled) && pooled.Samples >= RemoteLaneOfferPolicy.MinimumSamples
            ? pooled : null;
    }
}
