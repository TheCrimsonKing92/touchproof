using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Experimental acquisition. The retained full-content cache is still the default host path
// until the paired measurements and decision parity have been reviewed.
internal sealed record BoardFillSchedulingSnapshot(
    IReadOnlyList<BacklogItem> Candidates,
    IReadOnlyDictionary<string, BacklogItemStatus> Statuses,
    IReadOnlyList<BoardFillDuplicateSource> RecentSources)
{
    internal static BoardFillSchedulingSnapshot Read(string path, IReadOnlyList<BoardFillDraftRound> rounds,
        DateTimeOffset now, IEnumerable<string> planSources, Action<string, SqliteConnection>? observe = null,
        Action<string, SqliteCommand>? observeQuery = null)
    {
        if (!File.Exists(path)) return new([], new Dictionary<string, BacklogItemStatus>(), []);
        using var session = new SqliteReadSession(path, observe);
        var connection = session.Connection;
        var candidates = BoardFillBacklogSnapshot.ReadSchedulingCandidates(connection, observeQuery);
        var statuses = candidates.ToDictionary(item => item.Id, item => item.Status, StringComparer.Ordinal);
        var statusIds = candidates.SelectMany(item => item.Dependencies)
            .Where(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Backlog)
            .Select(dependency => dependency.PrerequisiteId)
            .Concat(planSources)
            .Except(statuses.Keys, StringComparer.Ordinal);
        // Materialize the key set before adding to the status dictionary.
        foreach (var batch in statusIds.ToArray().Chunk(400))
        {
            using var query = connection.CreateCommand();
            query.CommandText = $"SELECT id, status FROM backlog WHERE id IN ({SqliteReadSession.BindIds(query, batch)})";
            observeQuery?.Invoke("prerequisite", query);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                statuses.Add(reader.GetString(0), Enum.Parse<BacklogItemStatus>(reader.GetString(1)));
        }

        var recentIds = BoardFillNearDuplicate.RecentItemIds(rounds, now);
        var recentSources = candidates.Where(item => recentIds.Remove(item.Id))
            .Select(item => new BoardFillDuplicateSource(item.Id, item.Title, item.Body)).ToList();
        foreach (var batch in recentIds.Chunk(400))
        {
            using var query = connection.CreateCommand();
            query.CommandText = $"SELECT id, title, body FROM backlog WHERE id IN ({SqliteReadSession.BindIds(query, batch)})";
            observeQuery?.Invoke("historical", query);
            using var reader = query.ExecuteReader();
            while (reader.Read())
                recentSources.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return new(candidates, statuses, recentSources);
    }
}
