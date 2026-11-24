using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.ReadModels;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
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

    // Keep the synthetic decision comparison for detailed ordering and legacy fixtures.
    // The host comparison crosses the real portfolio, claim, landing and note stores.
    internal sealed class HostFixture : IDisposable
    {
        internal const string Claimed = "item-000005";
        internal const string Unassigned = "item-000007";
        internal const string LandedDependent = "item-000008";
        private const string LandedPrerequisite = "item-000009";
        internal const string DuplicateGoal = "11111111111111111111111111111111";
        internal const string ChangedHead = "new-head";
        private readonly string _root;
        private readonly ConductorBoardFillDraftStore _drafts;
        private readonly string _epicId;
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; }
        internal string MainHead { get; set; } = Head;

        internal HostFixture(BoardFillSnapshotTestDatabase source, int open)
        {
            _root = source.At($"host-{Guid.NewGuid():N}");
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            File.Copy(source.Path, Workspace.BacklogStorePath);
            using (var backlog = Connect(Workspace.BacklogStorePath))
                BoardFillSnapshotTestDatabase.Execute(backlog, $"""
                    UPDATE backlog SET tags='', pinned_at='{BoardFillSnapshotTestDatabase.Timestamp}' WHERE id='{Claimed}';
                    UPDATE backlog SET priority='high' WHERE id='{LandedDependent}';
                    UPDATE backlog SET updated_at='2000-01-01T00:00:00.0000000+00:00' WHERE id='{Held}';
                    UPDATE backlog_notes SET created_at='2000-01-01T00:00:00.0000000+00:00' WHERE backlog_item_id='{Held}';
                    DELETE FROM backlog_dependencies WHERE dependent_id IN ('{Claimed}', '{LandedPrerequisite}');
                    INSERT INTO backlog_dependencies (dependent_id, prerequisite_id, prerequisite_kind, created_at)
                    VALUES ('{LandedDependent}', '{LandedPrerequisite}', 'Backlog', '{BoardFillSnapshotTestDatabase.Timestamp}');
                    """);

            var portfolio = new PortfolioStore(Workspace.PortfolioStorePath);
            var epic = portfolio.AddEpicAsync("Scheduling comparison").GetAwaiter().GetResult();
            _epicId = epic.Id;
            var plans = new EpicPlanStore(Workspace.PortfolioStorePath);
            foreach (var id in PlanSources) plans.AddSliceAsync(epic.Id, id).GetAwaiter().GetResult();
            using (var connection = Connect(Workspace.PortfolioStorePath))
                BoardFillSnapshotTestDatabase.Execute(connection, $"""
                    WITH RECURSIVE ids(n) AS (VALUES (1) UNION ALL SELECT n+1 FROM ids WHERE n < {open})
                    INSERT OR IGNORE INTO backlog_epic_memberships (backlog_item_id, epic_id, created_at, created_by)
                    SELECT printf('item-%06d', n), '{epic.Id}', '{BoardFillSnapshotTestDatabase.Timestamp}', 'test'
                    FROM ids WHERE n <> 7
                    """);

            var kernel = new AgentOrchestratorKernel();
            var liveGoal = kernel.CreateGoal(new GoalId(DuplicateGoal), "Repair board scheduling\n\nSee src/Board.cs");
            var owner = kernel.CreateGoal(new GoalId("22222222222222222222222222222222"), "Delivered dependency");
            kernel.SetGoalSourceBacklogItemLink(owner.Id, LandedPrerequisite, SourceBacklogCoverage.Slice);
            var snapshot = kernel.ExportSnapshot();
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == owner.Id.Value
                    ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
            });
            owner = Kernel.Goals.Single(goal => goal.Id == owner.Id);
            GoalOperationJournal.Completed(Workspace.ExecutionDirectory, owner, "conductor:land", "Landed prerequisite fixture");
            _ = StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
            using (var connection = Connect(Workspace.SqliteStatePath))
                BoardFillSnapshotTestDatabase.Execute(connection, $"""
                    INSERT INTO source_backlog_claims (backlog_item_id, owner_goal_id, coverage, version, updated_at)
                    VALUES ('{Claimed}', '{liveGoal.Id.Value}', 'Full', 1, '{BoardFillSnapshotTestDatabase.Timestamp}')
                    """);

            _drafts = new(ConductorBoardFillDraftStore.DefaultPath(Workspace));
            var historical = new BacklogItem(Historical, "Repair board scheduling", "See src/Board.cs",
                BacklogItemStatus.Superseded, Now.AddDays(-2), Now.AddDays(-2), null);
            var round = _drafts.Begin(historical, Now.AddHours(-24));
            _drafts.Finish(round, new("draft", 0, Head, null, null, []), Now.AddHours(-24));
            _drafts.Assess(round.Id, new(new("unavailable", 0, []), [], new("none", []), false, []));
            _drafts.MarkReported(round.Id);
        }

        internal ConductorBoardFillHost CreateHost(Func<IReadOnlyList<BacklogItem>>? readBacklog = null,
            Func<IReadOnlyList<BoardFillDraftRound>, DateTimeOffset, IReadOnlyList<string>, IReadOnlyList<BacklogItem>>? readSchedulingBacklog = null,
            Action<string, string>? appendNote = null) =>
            ConductorBoardFillHost.CreateDefault(Workspace, () => ConductorAutonomyPolicy.Permissive with
            {
                BoardFillMode = ConductorBoardFillMode.Shadow, BoardFillTargetActiveGoals = 10
            }, () => MainHead, (_, _) => new("draft", 0, MainHead, null, null, []),
                readBacklog, readSchedulingBacklog, () => Now,
                (_, _) => ["src/Board.cs"], appendNote);

        internal HostDecision ReadDecision(SqliteConnection connection)
        {
            var selection = _drafts.ReadAll().SingleOrDefault(round => round.Outcome is null);
            var notes = new List<string>();
            var releasedStamp = false;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT n.backlog_item_id, n.text, n.created_at, b.updated_at
                    FROM backlog_notes n JOIN backlog b ON b.id=n.backlog_item_id
                    WHERE n.backlog_item_id IN ('{Held}', '{Duplicate}') ORDER BY n.id
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var text = reader.GetString(1);
                    if (text.StartsWith(BoardFillNearDuplicate.NotePrefix, StringComparison.Ordinal)
                        || text == ReleaseText)
                        notes.Add(text);
                    if (text == ReleaseText && selection is { } selected && selected.BacklogItemId == reader.GetString(0))
                        releasedStamp = selected.ItemNewestNoteAt == DateTimeOffset.Parse(reader.GetString(2)) &&
                            selected.ItemUpdatedAt == DateTimeOffset.Parse(reader.GetString(3));
                }
            }
            var readiness = File.ReadLines(Workspace.ConductEventsLogPath).Select(line =>
            {
                using var entry = JsonDocument.Parse(line);
                return entry.RootElement.GetProperty("eventKind").GetString() == "BOARD_FILL_READINESS"
                    ? entry.RootElement.GetProperty("detail").GetString() : null;
            }).Last(detail => detail is not null);
            return new(selection?.BacklogItemId, readiness!, notes.ToArray(), releasedStamp);
        }

        internal static string ReleaseText =>
            new BoardFillContradictionHold(Head, Now, [], "").ReleaseNote(ChangedHead);

        internal async Task CloseAndAssignAsync()
        {
            await new BacklogStore(Workspace.BacklogStorePath).CloseAsync(Unassigned);
            await new PortfolioStore(Workspace.PortfolioStorePath).AssignBacklogItemToEpicAsync(Unassigned, _epicId);
        }

        internal void ObserveProjectionQueries(Action<string, SqliteCommand> observeQuery) =>
            _ = BoardFillSchedulingSnapshot.ReadHostProjection(Workspace.BacklogStorePath, _drafts.ReadAll(), Now,
                [Unassigned], observeQuery: observeQuery);

        internal static SqliteConnection Connect(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    internal sealed record HostDecision(string? Selected, string Readiness, string[] Notes, bool SelectedReleaseStamp)
    {
        internal string Receipt() => JsonSerializer.Serialize(this);
    }
}
