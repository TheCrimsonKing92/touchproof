using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns a private SQLite database and TRX directory; no gates or git.
public sealed class MergeTrainMemberRecheckOutcomeCacheTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("member-recheck-cache-").FullName;
    private string Database => Path.Combine(root, "merge-train-acceptance.db");

    [Fact]
    public void Read_FreshDatabase_UsesVersionedTrainSetup()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var cache = new MergeTrainMemberRecheckOutcomeCache(Database);

        Assert.Null(cache.Read(fixture.Receipt));

        using var connection = Open();
        Assert.Equal(2, StoreSchemaVersions.Read(connection, StoreSchemaRegistry.MergeTrainAcceptance.StoreName));
        Assert.Equal(StoreSchemaState.Current, StoreSchemaVersions.Verify(connection, StoreSchemaRegistry.MergeTrainAcceptance));
        Assert.Empty(MergeTrainAcceptanceStore.OpenReadOnly(Database).ReadReceiptsSince(DateTimeOffset.MinValue));
    }

    [Fact]
    public void TrySave_Replay_PreservesFirstReceiptBoundOutcomeAfterTrxRemoval()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((member, _, selection, _) =>
            fixture.Execution(selection, member == fixture.Members[0]))!;
        var cache = new MergeTrainMemberRecheckOutcomeCache(Database);
        Assert.True(cache.TrySave(fixture.Receipt, outcome));
        var replacement = outcome with { Members = outcome.Members.Select(member => member with { CpuMilliseconds = 999 }).ToArray() };
        Assert.False(cache.TrySave(fixture.Receipt, replacement));
        foreach (var path in fixture.Receipt.GateTestResultPaths) File.Delete(path);

        var replay = new MergeTrainMemberRecheckOutcomeCache(Database).Read(fixture.Receipt)!;

        Assert.Equal(outcome.Members, replay.Members);
        Assert.Equal(outcome.FallbackReason, replay.FallbackReason);
        Assert.Equal(outcome.Implicated(fixture.Receipt), replay.Implicated(fixture.Receipt));
        Assert.Null(cache.Read(fixture.Receipt with { ReceiptId = "other-receipt" }));
        Assert.Null(cache.Read(fixture.Receipt with
        {
            Identity = Mcg.AgentOrchestrator.Core.MergeTrainIdentity.Create(fixture.Members,
                new string('c', 40), MergeTrainMemberRecheckFixture.Tree, "manifest")
        }));
    }

    [Fact]
    public void Open_NewerSchema_RefusesReadsAndWritesWithoutChanges()
    {
        using var fixture = new MergeTrainMemberRecheckFixture();
        var outcome = fixture.Run((member, _, selection, _) =>
            fixture.Execution(selection, member == fixture.Members[0]))!;
        MergeTrainAcceptanceStore.Setup(Database);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE store_schema_versions SET version = 3 WHERE store_name = 'merge-train-acceptance'";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var bytes = File.ReadAllBytes(Database);
        var cache = new MergeTrainMemberRecheckOutcomeCache(Database);

        Assert.Contains("schema is Newer", Assert.Throws<InvalidOperationException>(() => cache.Read(fixture.Receipt)).Message);
        Assert.Contains("schema is Newer", Assert.Throws<InvalidOperationException>(() => cache.TrySave(fixture.Receipt, outcome)).Message);

        Assert.Equal(bytes, File.ReadAllBytes(Database));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
