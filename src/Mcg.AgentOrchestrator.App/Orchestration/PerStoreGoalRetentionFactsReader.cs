using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PerStoreGoalRetentionFactsReader
{
    public static GoalRetentionStoreReadResult Read(
        IReadOnlyList<string> stores,
        Func<string, IReadOnlyList<StoredGoalRetentionFact>?> readStore,
        Func<string, UnavailableGoalStore> describeFailure)
    {
        var facts = new List<StoredGoalRetentionFact>();
        var unavailable = new List<UnavailableGoalStore>();
        var readable = 0;
        foreach (var store in stores)
        {
            // A failed read contributes nothing, even if its reader collected some rows.
            var storeFacts = readStore(store);
            if (storeFacts is null)
                unavailable.Add(describeFailure(store));
            else
            {
                readable++;
                facts.AddRange(storeFacts);
            }
        }
        return new(facts, readable, unavailable.OrderBy(store => store.Project, StringComparer.Ordinal)
            .ThenBy(store => store.Failure, StringComparer.Ordinal)
            .ThenBy(store => store.Path, StringComparer.Ordinal).ToArray());
    }
}
