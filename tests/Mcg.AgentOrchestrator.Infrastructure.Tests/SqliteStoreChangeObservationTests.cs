using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: no real project stores, environment mutation or shared process observations.
public sealed class SqliteStoreChangeObservationTests
{
    [Fact]
    public void StableObservationDetectsIndependentWalCommitsAndDoesNotPinReadTransaction()
    {
        using var f = new Store();
        using var writer = f.Open();
        Execute(writer, "PRAGMA journal_mode=WAL; CREATE TABLE sample(value); INSERT INTO sample VALUES(1);");
        using var observation = new SqliteStoreChangeObservation(f.Path);
        var before = observation.Observe();
        if (!OperatingSystem.IsWindows()) { Assert.Null(before); return; }
        Assert.NotNull(before);
        Assert.Equal(before, observation.Observe());
        Execute(writer, "UPDATE sample SET value=2;");
        var after = observation.Observe();
        Assert.NotEqual(before, after);
        Assert.Equal(after, observation.Observe());
        using var checkpoint = writer.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = checkpoint.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0)); // No observer transaction blocks checkpointing.
    }

    [Fact]
    public void MissingCreationFailureRecoveryAndConnectionEpochCannotValidateOldResult()
    {
        using var f = new Store();
        using var observation = new SqliteStoreChangeObservation(f.Path);
        var missing = observation.Observe();
        Assert.False(File.Exists(f.Path));
        File.WriteAllText(f.Path, "invalid database");
        Assert.Null(observation.Observe());
        File.Delete(f.Path);
        using (var writer = f.Open()) Execute(writer, "CREATE TABLE sample(value);");
        var created = observation.Observe();
        if (!OperatingSystem.IsWindows()) { Assert.Null(created); return; }
        Assert.NotNull(created);
        Assert.NotEqual(missing, created);
        observation.Dispose();
        using var reopened = new SqliteStoreChangeObservation(f.Path);
        Assert.NotEqual(created, reopened.Observe());
        Assert.Throws<ObjectDisposedException>(() => observation.Observe());
    }

    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }

    private sealed class Store : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("store-observation-").FullName;
        internal string Path => System.IO.Path.Combine(_root, "store.db");
        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
