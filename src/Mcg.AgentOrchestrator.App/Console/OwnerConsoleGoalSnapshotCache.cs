using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Accessed under the refresh snapshot owner's lock; only source objects are retained.
internal sealed class OwnerConsoleGoalSnapshotCache
{
    private readonly Dictionary<string, (string Version, OwnerConsoleLoadedGoals Loaded)> _entries = new(StringComparer.Ordinal);

    internal Dictionary<string, string> Observe(IReadOnlyList<GoalSummary> rows)
    {
        var versions = rows.ToDictionary(row => row.Id, row => row.UpdatedAt, StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Where(id => !versions.TryGetValue(id, out var version) ||
                     version != _entries[id].Version).ToArray()) _entries.Remove(id);
        return versions;
    }

    internal OwnerConsoleLoadedGoals? Find(string id, string version) =>
        _entries.TryGetValue(id, out var entry) && entry.Version == version ? entry.Loaded : null;

    internal void Store(OwnerConsoleLoadedGoals loaded, IReadOnlyDictionary<string, string> versions)
    {
        foreach (var goal in loaded.Goals)
            if (versions.TryGetValue(goal.Id.Value, out var version))
                _entries[goal.Id.Value] = (version, new([goal],
                    loaded.Requests.Where(request => request.GoalId == goal.Id).ToArray()));
    }
}
