namespace Mcg.AgentOrchestrator.App.Orchestration;

// Per-invocation observations; production scans do not allocate a probe.
internal sealed class ImpactReplayChangedSupportScanProbe
{
    internal Dictionary<string, int> TreePasses { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<(string Owner, string Identifier, bool AttributePosition)> BoundNames { get; } = [];

    internal void EnterTree(string path) => TreePasses[path] = TreePasses.GetValueOrDefault(path) + 1;

    internal void Bound(string owner, string identifier, bool attributePosition) =>
        BoundNames.Add((owner, identifier, attributePosition));
}
