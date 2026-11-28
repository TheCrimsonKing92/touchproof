using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class DeclaredTestInventoryRevisionStore
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Revision, string? ProjectHome), IReadOnlyList<AcceptanceTestClassDescriptor>> _inventories = new();

    internal int Reads { get; private set; }

    internal IReadOnlyList<AcceptanceTestClassDescriptor>? Read(string candidateRevision, string? projectHome,
        Func<IReadOnlyList<AcceptanceTestClassDescriptor>?> readCandidateInventory)
    {
        var key = (candidateRevision, projectHome);
        lock (_sync)
        {
            if (_inventories.TryGetValue(key, out var remembered)) return remembered;
            Reads++;
        }

        var inventory = readCandidateInventory();
        if (inventory is not null)
        {
            lock (_sync) _inventories[key] = inventory;
        }

        return inventory;
    }
}
