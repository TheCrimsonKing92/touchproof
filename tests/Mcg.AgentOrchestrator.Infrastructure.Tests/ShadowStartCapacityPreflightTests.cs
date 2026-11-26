using Microsoft.Data.Sqlite;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class ShadowStartCapacityPreflightTests
{
    [Fact]
    public void SecondInvocationCannotPreflightOrStartWhileRegistryIsLeased()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        using var held = new ShadowStartRunRegistry(fixture.Registry);
        var marker = fixture.Scratch("candidate-started");
        var hooks = fixture.Hooks with
        {
            AvailableBytes = _ => throw new InvalidOperationException("Capacity ran without the registry lease.")
        };

        var result = fixture.Run("normal", "scratch", extra: ["--scratch-retention=disposable"], hooks: hooks, marker: marker);

        Assert.Equal(1, result.Exit);
        Assert.Contains("assertion=concurrent-run", result.Error);
        Assert.False(Directory.Exists(fixture.Scratch("scratch")));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void StagingShortfallLeavesWalSourceAndExecutionUntouched()
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        using var live = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Source, "state.db"), Pooling = false
        }.ToString());
        live.Open();
        using var setup = live.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=wal; CREATE TABLE live (value); INSERT INTO live VALUES (17);";
        setup.ExecuteNonQuery();
        var before = Snapshot(fixture.Source);
        Assert.Contains(before.Keys, path => path.EndsWith("-wal", StringComparison.Ordinal));
        var marker = fixture.Scratch("candidate-started");
        var hooks = fixture.Hooks with
        {
            MapVolume = path => path.StartsWith(fixture.Staging, StringComparison.Ordinal) ? new("temp", "T:") : new("copy", "S:"),
            AvailableBytes = volume => volume.Identity == "temp" ? 0 : long.MaxValue,
            BeforeCopy = _ => throw new InvalidOperationException("Copy ran before capacity admission.")
        };

        var result = fixture.Run("normal", "scratch", extra: ["--scratch-retention", "disposable"], hooks: hooks, marker: marker);

        Assert.Equal(1, result.Exit);
        Assert.Contains("assertion=capacity-preflight", result.Error);
        Assert.Contains("volume=T: required_bytes=", result.Error);
        Assert.Contains("available_bytes=0 shortfall_bytes=", result.Error);
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(fixture.Scratch("scratch")));
        Assert.False(Directory.Exists(fixture.Staging));
        Assert.Empty(Directory.EnumerateFiles(fixture.Registry, "*.run.json"));
        var after = Snapshot(fixture.Source);
        Assert.Equal(before.ToArray(), after.ToArray());
        setup.CommandText = "SELECT value FROM live";
        Assert.Equal(17L, setup.ExecuteScalar());
    }

    private static SortedDictionary<string, (long Length, DateTime Modified, string Content)> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, (long, DateTime, string)>();
        foreach (var file in Directory.EnumerateFiles(root))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            snapshot.Add(file, (stream.Length, File.GetLastWriteTimeUtc(file),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))));
        }
        return snapshot;
    }
}
