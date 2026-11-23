using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static BoardFillSnapshotTestDatabase;

public sealed class BoardFillBacklogSnapshotReuseTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private const string WindowsFileIdentity = "Backlog file identity requires the Windows SQLite VFS.";

    [Fact(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    public void Cold_hit_change_restart_and_copied_replacement_have_distinct_lifecycles()
    {
        using var database = new BoardFillSnapshotTestDatabase();
        using var cache = new BoardFillBacklogSnapshotCache(database.Path);
        var before = File.ReadAllBytes(database.Path);
        var cold = cache.Read();
        Assert.Same(cold, cache.Read());
        Assert.Equal(before, File.ReadAllBytes(database.Path));
        var item = Assert.Single(cold, item => item.Id == ItemId);
        Assert.Throws<NotSupportedException>(() => ((IList<BacklogItem>)cold).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BacklogNote>)item.Notes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<BacklogDependency>)item.Dependencies).Clear());

        database.Run(Mutation("item", 1));
        var changed = cache.Read();
        Assert.NotSame(cold, changed);
        Assert.Equal("Title 1", changed.Single(item => item.Id == ItemId).Title);
        Assert.Same(changed, cache.Read());
        using (var restart = new BoardFillBacklogSnapshotCache(database.Path))
        {
            var fresh = restart.Read();
            Assert.NotSame(changed, fresh);
            Assert.Same(fresh, restart.Read());
            Assert.Equal(Contents(changed), Contents(fresh));
        }

        database.Run("PRAGMA wal_checkpoint(TRUNCATE)");
        var proof = ReadProof(database.Path);
        var copy = database.At("copy.db");
        File.Copy(database.Path, copy);
        File.Move(database.Path, database.At("retired.db"));
        File.Move(copy, database.Path);
        Assert.Equal(proof, ReadProof(database.Path));
        var replacement = cache.Read();
        Assert.NotSame(changed, replacement);
        Assert.Equal(Contents(changed), Contents(replacement));
        Assert.Same(replacement, cache.Read());

        cache.Dispose();
        using var exclusive = File.Open(database.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    [InlineData("item", false)]
    [InlineData("note", false)]
    [InlineData("dependency", false)]
    [InlineData("item", true)]
    [InlineData("note", true)]
    [InlineData("dependency", true)]
    public async Task Independent_commits_are_visible_on_the_correct_side_of_snapshot_establishment(string part, bool warm)
    {
        using var database = new BoardFillSnapshotTestDatabase();
        var established = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        var block = false;
        var trace = new BoardFillSnapshotSqlTrace(() =>
        {
            if (!block || !established.TrySetResult()) return;
            if (!proceed.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Signal did not occur: independent commit and snapshot release.");
        });
        using var cache = new BoardFillBacklogSnapshotCache(database.Path, trace.Attach);
        var original = Contents(BoardFillBacklogSnapshot.Read(database.Path));
        database.Run(Mutation(part, 1));
        var before = Contents(BoardFillBacklogSnapshot.Read(database.Path));
        Assert.NotEqual(original, before);
        var warmed = warm ? cache.Read() : null;

        block = true;
        var inFlight = Task.Run(cache.Read);
        var requestedNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<BacklogItem>> nextInFlight;
        try
        {
            await established.Task.WaitAsync(TimeSpan.FromSeconds(30));
            database.Run(Mutation(part, 2));
            nextInFlight = Task.Run(() =>
            {
                requestedNext.SetResult();
                return cache.Read();
            });
            await requestedNext.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { proceed.Set(); }
        var acquired = await inFlight.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(trace.Failure);
        Assert.Equal(before, Contents(acquired));
        if (warm) Assert.Same(warmed, acquired);

        var after = Contents(BoardFillBacklogSnapshot.Read(database.Path));
        Assert.NotEqual(before, after);
        var next = await nextInFlight.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(after, Contents(next));
        Assert.Same(next, cache.Read());
    }

    [Fact(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    public void Unsupported_schema_or_invalid_token_cannot_authorize_a_hit()
    {
        using var database = new BoardFillSnapshotTestDatabase();
        using var cache = new BoardFillBacklogSnapshotCache(database.Path);
        var warm = cache.Read();
        Assert.Same(warm, cache.Read());
        database.Run("UPDATE store_schema_versions SET version = 5 WHERE store_name = 'backlog'");
        var unsupported = cache.Read();
        Assert.NotSame(warm, unsupported);
        Assert.NotSame(unsupported, cache.Read());
        Assert.Equal(Contents(BoardFillBacklogSnapshot.Read(database.Path)), Contents(unsupported));
        database.Run("""
            UPDATE store_schema_versions SET version = 4 WHERE store_name = 'backlog';
            UPDATE backlog_snapshot_revision SET revision = 'invalid';
            """);
        var invalid = cache.Read();
        Assert.NotSame(invalid, cache.Read());
        Assert.Equal(Contents(BoardFillBacklogSnapshot.Read(database.Path)), Contents(invalid));
    }

    [Fact(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    public void Removed_coverage_falls_back_and_preserves_full_read_failures_and_missing_file_behavior()
    {
        using var database = new BoardFillSnapshotTestDatabase();
        using var cache = new BoardFillBacklogSnapshotCache(database.Path);
        var warm = cache.Read();
        Assert.Same(warm, cache.Read());
        database.Run($"DROP TRIGGER {NoteTrigger}; {Mutation("note", 1)}");
        var fallback = cache.Read();
        Assert.NotSame(warm, fallback);
        Assert.NotSame(fallback, cache.Read());
        Assert.Equal(Contents(BoardFillBacklogSnapshot.Read(database.Path)), Contents(fallback));
        Assert.Contains(fallback.Single(item => item.Id == ItemId).Notes, note => note.Text == "Evidence 1");

        BacklogStore.Setup(database.Path);
        var repaired = cache.Read();
        Assert.Same(repaired, cache.Read());
        database.Run("DROP TABLE backlog_dependencies");
        var expected = Assert.Throws<SqliteException>(() => BoardFillBacklogSnapshot.Read(database.Path));
        var actual = Assert.Throws<SqliteException>(cache.Read);
        Assert.Equal(expected.SqliteErrorCode, actual.SqliteErrorCode);
        Assert.Equal(expected.Message, actual.Message);

        File.Move(database.Path, database.At("broken.db"));
        Assert.Empty(cache.Read());
        File.WriteAllText(database.Path, "invalid SQLite file");
        expected = Assert.Throws<SqliteException>(() => BoardFillBacklogSnapshot.Read(database.Path));
        actual = Assert.Throws<SqliteException>(cache.Read);
        Assert.Equal(expected.Message, actual.Message);
        File.Delete(database.Path);
        using var replacement = new BoardFillSnapshotTestDatabase();
        File.Copy(replacement.Path, database.Path);
        var recovered = cache.Read();
        Assert.Equal(Contents(BoardFillBacklogSnapshot.Read(database.Path)), Contents(recovered));
        Assert.Same(recovered, cache.Read());
    }

    [Fact(Skip = WindowsFileIdentity, SkipUnless = nameof(IsWindows))]
    public void Restoring_identical_trigger_SQL_does_not_hide_an_untracked_commit()
    {
        using var database = new BoardFillSnapshotTestDatabase();
        using var cache = new BoardFillBacklogSnapshotCache(database.Path);
        var original = cache.Read();
        var proof = ReadProof(database.Path);
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT sql FROM sqlite_schema WHERE name = '{NoteTrigger}'";
            var create = Assert.IsType<string>(command.ExecuteScalar());
            Execute(connection, $"BEGIN IMMEDIATE; DROP TRIGGER {NoteTrigger}; {Mutation("note", 1)}; {create}; COMMIT;");
        }
        var restored = ReadProof(database.Path);
        Assert.Equal(proof.Revision, restored.Revision);
        Assert.NotEqual(proof.SchemaCookie, restored.SchemaCookie);
        var refreshed = cache.Read();
        Assert.NotSame(original, refreshed);
        Assert.Contains(refreshed.Single(item => item.Id == ItemId).Notes, note => note.Text == "Evidence 1");
        Assert.Same(refreshed, cache.Read());
    }

    private static BacklogSnapshotRevision ReadProof(string path)
    {
        using var session = new SqliteReadSession(path);
        return Assert.IsType<BacklogSnapshotRevision>(BacklogSnapshotRevision.TryRead(session.Connection));
    }
}
