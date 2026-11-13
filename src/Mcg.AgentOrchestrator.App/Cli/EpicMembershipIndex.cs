using System.Collections.Frozen;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

// Direct memberships captured once for a listing invocation; row lookups never access the store.
internal static class EpicMembershipIndex
{
    internal sealed record Membership(string EpicId, string EpicTitle)
    {
        internal string ShortId => EpicId.Length >= 8 ? EpicId[..8] : EpicId;
    }

    internal sealed class Lookup(
        FrozenDictionary<string, Membership> goals,
        FrozenDictionary<string, Membership> backlogItems)
    {
        internal Membership? FindGoal(string id) => goals.GetValueOrDefault(id);
        internal Membership? FindBacklogItem(string id) => backlogItems.GetValueOrDefault(id);
    }

    internal static Lookup Empty { get; } = new(
        FrozenDictionary<string, Membership>.Empty, FrozenDictionary<string, Membership>.Empty);

    internal static Lookup Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        PortfolioStore store;
        try { store = PortfolioStore.OpenReadOnly(path); }
        catch (InvalidOperationException) { return Empty; } // Missing or non-current schema.
        catch (SqliteException ex) when (ex.SqliteErrorCode == 14 && !File.Exists(path)) { return Empty; }

        var goals = new Dictionary<string, Membership>(StringComparer.Ordinal);
        var backlogItems = new Dictionary<string, Membership>(StringComparer.Ordinal);
        foreach (var epic in store.ListEpicsAsync().GetAwaiter().GetResult())
        {
            var membership = new Membership(epic.Id, epic.Title);
            foreach (var member in store.ListEpicMembersAsync(epic.Id).GetAwaiter().GetResult())
            {
                var members = member.Kind switch
                {
                    PortfolioMemberKind.Goal => goals,
                    PortfolioMemberKind.BacklogItem => backlogItems,
                    _ => throw new InvalidOperationException($"Unknown portfolio member kind: {member.Kind}.")
                };
                members.Add(member.MemberId, membership);
            }
        }
        return new Lookup(goals.ToFrozenDictionary(StringComparer.Ordinal),
            backlogItems.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
