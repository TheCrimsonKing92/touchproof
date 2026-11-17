using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns a unique directory, filters policy events by exact path and disables pooling.
public sealed class StoreSetupConnectionPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "store-setup-policy-" + Guid.NewGuid().ToString("N"));

    [Xunit.Theory]
    [Xunit.InlineData("judge-panel")]
    [Xunit.InlineData("steward-triggers")]
    [Xunit.InlineData("progressive-review-steering")]
    public void SetupOpensOneStrictPolicyConnection(string storeName)
    {
        var path = Path.GetFullPath(Path.Combine(_directory, storeName + ".db"));
        SetupAndAssertPolicyEvent(storeName, path);

        // These pragmas are per connection; the setup event above proves the policy route.
        using var connection = SqliteStoreConnectionPolicy.Open(
            path, readOnly: false, SqliteStoreDurability.Strict, foreignKeys: false,
            operationTag: "StoreSetupConnectionPolicyTests.Verify");
        Assert.Equal(2L, ReadPragma(connection, "synchronous"));
        Assert.Equal(67_108_864L, ReadPragma(connection, "journal_size_limit"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("judge-panel")]
    [Xunit.InlineData("steward-triggers")]
    [Xunit.InlineData("progressive-review-steering")]
    public void SetupReleasesFileAndRerunPreservesVersionRowAndBytes(string storeName)
    {
        var path = Path.GetFullPath(Path.Combine(_directory, storeName + ".db"));
        SetupAndAssertPolicyEvent(storeName, path);
        var versionRow = ReadVersionRow(path, storeName);
        var bytes = File.ReadAllBytes(path);

        SetupAndAssertPolicyEvent(storeName, path);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(versionRow, ReadVersionRow(path, storeName));
        Assert.True(File.Exists(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            Assert.False(File.Exists(path + suffix));
        Directory.Delete(_directory);
        Assert.False(Directory.Exists(_directory));
    }

    private static void SetupAndAssertPolicyEvent(string storeName, string databasePath)
    {
        var (setup, expectedTag) = storeName switch
        {
            "judge-panel" => ((Action<string>)JudgePanelStoreSetup.Setup, "JudgePanelStoreSetup.Setup"),
            "steward-triggers" => ((Action<string>)StewardTriggerStoreSetup.Setup, "StewardTriggerStoreSetup.Setup"),
            "progressive-review-steering" => ((Action<string>)ProgressiveReviewSteeringStoreSetup.Setup,
                "ProgressiveReviewSteeringStoreSetup.Setup"),
            _ => throw new ArgumentOutOfRangeException(nameof(storeName))
        };
        var events = new ConcurrentQueue<(string Tag, string Path)>();
        void Observe(string tag, string path)
        {
            if (path == databasePath)
                events.Enqueue((tag, path));
        }

        SqliteStoreConnectionPolicy.ConnectionOpenedForDiagnostics += Observe;
        try
        {
            setup(databasePath);
        }
        finally
        {
            SqliteStoreConnectionPolicy.ConnectionOpenedForDiagnostics -= Observe;
        }

        Assert.Equal((expectedTag, databasePath), Assert.Single(events));
    }

    private static (string StoreName, int Version, string AppliedAt) ReadVersionRow(string path, string storeName)
    {
        using var connection = SqliteStoreConnectionPolicy.Open(
            path, readOnly: true, SqliteStoreDurability.Strict, foreignKeys: false,
            operationTag: "StoreSetupConnectionPolicyTests.ReadVersion");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT store_name, version, applied_at FROM store_schema_versions WHERE store_name = $store";
        command.Parameters.AddWithValue("$store", storeName);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var row = (reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
        Assert.False(reader.Read());
        return row;
    }

    private static long ReadPragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA " + name;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
