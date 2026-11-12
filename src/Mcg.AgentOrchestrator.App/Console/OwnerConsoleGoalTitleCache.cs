using System.Collections.Immutable;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGoalTitleCache(Func<string?, string>? title = null)
{
    private readonly Func<string?, string> _title = title ?? OwnerGoalTitle.Full;
    private ImmutableDictionary<string, (string Title, string UpdatedAt, long Observed)> _entries =
        ImmutableDictionary.Create<string, (string, string, long)>(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> _titles = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);
    private long _observation;

    internal IReadOnlyDictionary<string, string> Titles => _titles;

    internal void Retain(IEnumerable<string> ids, int recentMissing)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentMissing);
        var live = ids.ToHashSet(StringComparer.Ordinal);
        // The tail may deliver a goal's final events after it leaves the bounded board query.
        var history = _entries.Where(item => !live.Contains(item.Key))
            .OrderByDescending(item => item.Value.Observed).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(recentMissing).Select(item => item.Key).ToArray();
        live.UnionWith(history);
        _entries = _entries.RemoveRange(_entries.Keys.Where(id => !live.Contains(id)));
        _titles = _titles.RemoveRange(_titles.Keys.Where(id => !live.Contains(id)));
    }

    internal void Merge(IReadOnlyList<GoalSummary> rows, bool force = false)
    {
        var entries = _entries;
        var titles = _titles;
        var observed = ++_observation;
        foreach (var row in rows)
        {
            var value = !force && entries.TryGetValue(row.Id, out var entry) && entry.UpdatedAt == row.UpdatedAt
                ? entry.Title : _title(row.Objective);
            entries = entries.SetItem(row.Id, (value, row.UpdatedAt, observed));
            titles = titles.SetItem(row.Id, value);
        }
        _entries = entries;
        _titles = titles;
    }

    internal string Title(string? idOrPrefix)
    {
        if (string.IsNullOrEmpty(idOrPrefix)) return string.Empty;
        var titles = _titles;
        var exact = titles.FirstOrDefault(item => item.Key.Equals(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact.Key is not null) return exact.Value;
        var matches = titles.Where(item => item.Key.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : string.Empty;
    }
}
