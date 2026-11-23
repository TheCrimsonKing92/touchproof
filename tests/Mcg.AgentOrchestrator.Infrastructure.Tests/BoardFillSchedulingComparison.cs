using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.ReadModels;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class BoardFillSchedulingComparison
{
    internal const string Duplicate = "item-000001";
    internal const string Dependent = "item-000002";
    internal const string Held = "item-000003";
    internal const string Alternate = "item-000004";
    internal const string Prerequisite = "closed-prerequisite";
    internal const string Historical = "closed-historical";
    internal const string Irrelevant = "closed-irrelevant";
    internal const string ClosedPlanSource = "closed-plan";
    internal const string Missing = "missing-prerequisite";
    internal const string Head = "base-head";
    internal static readonly DateTimeOffset Now = new(2026, 11, 24, 14, 6, 0, TimeSpan.Zero);

    private static readonly BoardFillDraftRound[] Rounds =
        [Round(Historical, Now.AddHours(-24)), Round("expired", Now.AddHours(-24).AddTicks(-1)),
            Round("future", Now.AddTicks(1)), Round("recent-missing", Now)];
    private static readonly string[] PlanSources = [Dependent, Alternate, ClosedPlanSource];
    private static readonly EpicPlan Plan = new("epic-a", null, PlanSources
        .Select((id, position) => new EpicPlanItem(position, EpicPlanItemKind.Slice, id, null, false)).ToArray(), []);
    private static readonly EpicPlanGoalClaims NoClaims = new(new Dictionary<string, EpicPlanSourceClaim>(),
        new Dictionary<string, EpicPlanLinkedGoal>(), new Dictionary<string, string>());
    private static readonly Dictionary<string, GoalSummary> NoGoalSummaries = new();

    internal static BoardFillSnapshotTestDatabase Create(int total, int open)
    {
        var database = new BoardFillSnapshotTestDatabase(total - 6);
        try
        {
            const string timestamp = BoardFillSnapshotTestDatabase.Timestamp;
            database.Run($"""
                UPDATE backlog SET status = CASE WHEN rowid % 2 = 0 THEN 'Done' ELSE 'Superseded' END,
                    body = hex(zeroblob(1490)) WHERE rowid > {open};
                UPDATE backlog_notes SET text = hex(zeroblob(250))
                    WHERE backlog_item_id IN (SELECT id FROM backlog WHERE status <> 'Open');
                UPDATE backlog SET title = 'Repair board scheduling', body = 'See src/Board.cs', priority = 'high',
                    pinned_at = '{timestamp}' WHERE id = '{Duplicate}';
                UPDATE backlog SET title = 'Account for prerequisite', priority = 'high' WHERE id = '{Dependent}';
                UPDATE backlog SET priority = 'high' WHERE id = '{Alternate}';
                UPDATE backlog SET pinned_at = '{timestamp}' WHERE id = '{Held}';
                UPDATE backlog SET tags = 'owner-gated' WHERE id = 'item-000005';
                DELETE FROM backlog_dependencies WHERE dependent_id IN ('{Duplicate}', '{Held}', 'item-000007');
                INSERT INTO backlog (id, title, body, status, created_at, updated_at) VALUES
                    ('{Historical}', 'Repair board scheduling', 'See src/Board.cs', 'Superseded', '{timestamp}', '{timestamp}'),
                    ('expired', 'Repair board scheduling', 'See src/Board.cs', 'Done', '{timestamp}', '{timestamp}'),
                    ('future', 'Repair board scheduling', 'See src/Board.cs', 'Done', '{timestamp}', '{timestamp}'),
                    ('{Prerequisite}', 'Prerequisite', hex(zeroblob(16384)), 'Done', '{timestamp}', '{timestamp}'),
                    ('{Irrelevant}', 'Unrelated', hex(zeroblob(16384)), 'Done', '{timestamp}', '{timestamp}'),
                    ('{ClosedPlanSource}', 'Closed plan source', hex(zeroblob(16384)), 'Superseded', '{timestamp}', '{timestamp}');
                INSERT INTO backlog_dependencies (dependent_id, prerequisite_id, prerequisite_kind, created_at) VALUES
                    ('{Dependent}', '{Prerequisite}', 'Backlog', '{timestamp}'),
                    ('item-000006', '{Missing}', 'Backlog', '{timestamp}'),
                    ('item-000006', 'missing-goal', 'Goal', '{timestamp}');
                """);
            SetNote(database, Held, Marker());
            database.Run("PRAGMA wal_checkpoint(TRUNCATE)");
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    internal static string Marker() => $"{BoardFillContradictionHold.Prefix}\nmain-head: {Head}\n" +
        $"item-change-stamp: {Now.AddDays(-1):O}\nevidence: src/Board.cs\nclaims:\na contradicted claim";

    internal static void SetNote(BoardFillSnapshotTestDatabase database, string id, string text)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backlog_notes SET text = $text WHERE backlog_item_id = $id";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    internal static BoardFillSchedulingSnapshot ReadProjection(string path,
        Action<string, SqliteConnection>? observe = null, Action<string, SqliteCommand>? query = null) =>
        BoardFillSchedulingSnapshot.Read(path, Rounds, Now, PlanSources, observe, query);

    internal static Decision DecideFull(IReadOnlyList<BacklogItem> items)
    {
        var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return Decide(items, byId, item => BacklogDependencyReadiness.Evaluate(item, _ => null,
                id => byId.GetValueOrDefault(id), _ => new(null), _ => "Running"),
            BoardFillNearDuplicate.Lookup(items, [], Rounds, Now));
    }

    internal static Decision DecideProjected(BoardFillSchedulingSnapshot snapshot)
    {
        var byId = snapshot.Candidates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        BacklogItemStatus? Status(string id) => snapshot.Statuses.TryGetValue(id, out var status) ? status : null;
        return Decide(snapshot.Candidates, byId, item => BacklogDependencyReadiness.EvaluateWithPrerequisites(item,
                _ => null, id => Status(id) is { } status ? (id, status) : null, _ => new(null), _ => "Running"),
            BoardFillNearDuplicate.LookupSources(snapshot.RecentSources, []), Status);
    }

    private static Decision Decide(IReadOnlyList<BacklogItem> items, IReadOnlyDictionary<string, BacklogItem> byId,
        Func<BacklogItem, BacklogReadiness> readiness, Func<BacklogItem, BoardFillNearDuplicateReference?> duplicate,
        Func<string, BacklogItemStatus?>? prerequisiteStatus = null)
    {
        var excluded = ConductorBoardFillDraftStore.AlreadyDrafted(items, Head, Rounds);
        var candidates = BoardFillReadyItemSelector.EvaluateCandidates(items, [], excluded, readiness, Rounds, Head).ToArray();
        var preferred = EpicPlanReadySliceSelector.Order([new(Plan,
            EpicPlanStatusReader.Compute(Plan, byId, NoGoalSummaries, NoClaims, findPrerequisiteStatus: prerequisiteStatus), [])]);
        var members = items.Where(item => item.Id != "item-000007").Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var missingMembers = new List<string>();
        var duplicates = new List<DuplicateObservation>();
        var ordered = BoardFillReadyItemSelector.OrderReady(candidates, preferred, members,
            item => missingMembers.Add(item.Id), duplicate, (item, reference) =>
            {
                var annotation = item.Notes.Any(note =>
                    note.Text.Contains(BoardFillNearDuplicate.Token(reference), StringComparison.Ordinal))
                    ? null : BoardFillNearDuplicate.Note(reference);
                duplicates.Add(new(item.Id, reference, annotation));
            }).Select(item => item.Id).ToArray();
        var holds = items.Where(item => item.Status == BacklogItemStatus.Open)
            .Select(item => new HoldObservation(item.Id, BoardFillReadyItemSelector.IsOwnerGated(item),
                BoardFillReadyItemSelector.IsAuthorRefused(item), BoardFillContradictionHold.Blocks(item, Head, null),
                BoardFillReadyItemSelector.ChangeStamp(item))).ToArray();
        var reasons = candidates.Where(candidate => !candidate.Ready.Eligible)
            .GroupBy(candidate => candidate.Ready.Suffix, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count());
        return new(ordered.FirstOrDefault(), ordered, preferred,
            candidates.Select(candidate => new ReadinessObservation(candidate.Item.Id, candidate.Ready)).ToArray(),
            reasons, holds, duplicates, missingMembers);
    }

    private static BoardFillDraftRound Round(string id, DateTimeOffset startedAt) =>
        new(id, id, Now.AddDays(-2), null, null, "cancelled", null, null, [], startedAt, startedAt);

    internal sealed record Decision(string? Selected, IReadOnlyList<string> Order, IReadOnlyList<string> Preferred,
        IReadOnlyList<ReadinessObservation> Candidates, IReadOnlyDictionary<string, int> Reasons,
        IReadOnlyList<HoldObservation> Holds, IReadOnlyList<DuplicateObservation> Duplicates, IReadOnlyList<string> MissingMembers)
    {
        internal string Receipt() => JsonSerializer.Serialize(this);
    }

    internal sealed record ReadinessObservation(string Id, BacklogReadiness Readiness);
    internal sealed record HoldObservation(string Id, bool OwnerGated, bool AuthorRefused, bool Contradicted,
        DateTimeOffset ChangeStamp);
    internal sealed record DuplicateObservation(string Id, BoardFillNearDuplicateReference Reference, string? Annotation);
}
