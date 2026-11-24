using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One read-only snapshot for the board-fill scheduling decision.
internal sealed record BoardFillSchedulingSnapshot(
    IReadOnlyList<BacklogItem> Candidates,
    IReadOnlyDictionary<string, BacklogItemStatus> Statuses,
    IReadOnlyList<BoardFillDuplicateSource> RecentSources)
{
    internal static IReadOnlyList<BacklogItem> ReadHostProjection(string path,
        IReadOnlyList<BoardFillDraftRound> rounds, DateTimeOffset now, IReadOnlyList<string> membershipQuestionIds,
        Action<string, SqliteConnection>? observe = null, Action<string, SqliteCommand>? observeQuery = null)
    {
        var projection = Read(path, rounds, now, membershipQuestionIds, observe, observeQuery);
        var references = projection.RecentSources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        var open = projection.Candidates.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        // Closed rows can satisfy prerequisites, match recent drafts or resolve membership questions.
        // A closed plan source cannot rank, and every open plan source is already a candidate.
        var closed = projection.Statuses.Keys.Union(references.Keys, StringComparer.Ordinal).Except(open, StringComparer.Ordinal);
        return [.. projection.Candidates, .. closed.Select(id =>
        {
            var reference = references.GetValueOrDefault(id);
            return new BacklogItem(id, reference?.Title ?? "", reference?.Body ?? "",
                projection.Statuses.GetValueOrDefault(id, BacklogItemStatus.Done), default, default, null);
        })];
    }

    internal static BoardFillSchedulingSnapshot Read(string path, IReadOnlyList<BoardFillDraftRound> rounds,
        DateTimeOffset now, IEnumerable<string> additionalStatusIds, Action<string, SqliteConnection>? observe = null,
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
            .Concat(additionalStatusIds)
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
