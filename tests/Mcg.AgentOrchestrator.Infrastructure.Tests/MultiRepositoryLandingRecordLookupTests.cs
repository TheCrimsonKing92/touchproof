using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns a private SQLite database and disposes every connection.
public sealed class MultiRepositoryLandingRecordLookupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("landing-lookup-").FullName;
    private MultiRepositoryLandingRecordStore Store => new(_root);
    private static readonly string HomeCommit = new('a', 40);

    [Fact]
    public void MissingDatabase_ReturnsNoMatchWithoutCreatingFile()
    {
        Assert.Empty(Store.FindByLandedHomeCommit(HomeCommit));
        Assert.False(File.Exists(Store.DatabasePath));
    }

    [Fact]
    public void MissingTable_ReturnsNoMatchWithoutChangingDatabase()
    {
        Execute("CREATE TABLE unrelated(value TEXT)");
        var before = File.ReadAllBytes(Store.DatabasePath);

        Assert.Empty(Store.FindByLandedHomeCommit(HomeCommit));

        Assert.Equal(before, File.ReadAllBytes(Store.DatabasePath));
    }

    [Fact]
    public void ExistingPayloads_AllMatchesReturnInGoalOrderWithoutWrites()
    {
        // Write the pre-existing schema and payload directly, without the store's creating path.
        Execute("CREATE TABLE landings(goal TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(goal,kind))");
        var earlier = CreateRecord(new string('1', 32), HomeCommit);
        var later = CreateRecord(new string('2', 32), HomeCommit);
        Insert(later);
        Insert(CreateRecord(new string('3', 32), new string('b', 40)));
        Insert(earlier);
        Execute("INSERT INTO landings VALUES('ignored','attempt','not-json')");
        var before = File.ReadAllBytes(Store.DatabasePath);

        var found = Store.FindByLandedHomeCommit(HomeCommit.ToUpperInvariant());

        Assert.Equal(new[] { earlier.GoalId, later.GoalId }, found.Select(record => record.GoalId));
        Assert.All(found, record => Assert.Equal(new[] { "companion", "default" }, record.Order));
        Assert.Equal("landed-companion", found[0].LandedCommits["companion"]);
        Assert.Equal(before, File.ReadAllBytes(Store.DatabasePath));
        Assert.Empty(Store.FindByLandedHomeCommit(new string('c', 40)));
    }

    [Fact]
    public void NoLandedRows_ReturnsNoMatch()
    {
        Execute("CREATE TABLE landings(goal TEXT, kind TEXT, payload TEXT)");
        Execute("INSERT INTO landings VALUES('ignored','failed','not-json')");
        Assert.Empty(Store.FindByLandedHomeCommit(HomeCommit));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void UnreadableLandedPayload_FailsInsteadOfReturningNoMatch(string payload)
    {
        Execute("CREATE TABLE landings(goal TEXT, kind TEXT, payload TEXT)");
        Execute("INSERT INTO landings VALUES('broken','landed',$payload)", payload);
        var error = Record.Exception(() => Store.FindByLandedHomeCommit(HomeCommit));
        Assert.True(error is JsonException or InvalidDataException, error?.ToString() ?? "Lookup silently returned no match.");
    }

    [Fact]
    public void UnreadableDatabase_FailsInsteadOfReturningNoMatch()
    {
        File.WriteAllText(Store.DatabasePath, "not a SQLite database");
        Assert.Throws<SqliteException>(() => Store.FindByLandedHomeCommit(HomeCommit));
    }

    private static MultiRepositoryLandingRecord CreateRecord(string id, string home) => new(new GoalId(id), "tuple",
        new Dictionary<string, string> { ["default"] = home, ["companion"] = "landed-companion" },
        new Dictionary<string, string>(), ["companion", "default"], DateTimeOffset.UnixEpoch);

    private void Insert(MultiRepositoryLandingRecord record) =>
        Execute($"INSERT INTO landings VALUES('{record.GoalId.Value}','landed',$payload)", JsonSerializer.Serialize(record));

    private void Execute(string sql, string? payload = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Store.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (payload is not null) command.Parameters.AddWithValue("$payload", payload);
        command.ExecuteNonQuery();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
