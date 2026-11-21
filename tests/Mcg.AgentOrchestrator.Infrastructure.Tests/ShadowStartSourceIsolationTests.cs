using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe. Each case uses its own databases and scratch tree.
public sealed class ShadowStartSourceIsolationTests
{
    [Fact]
    public void SuccessfulRun_LeavesSourceHashesAndWriteTimesUnchanged()
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        fixture.CreateDatabase("cohort-acceptance.db", journalMode: "wal");
        fixture.CreateDatabase("impact-replay.checkpoint.db");
        fixture.CreateDatabase(Path.Combine("older-backup", "state.db"));
        File.WriteAllText(Path.Combine(fixture.Source, "configuration.json"), "{}");
        var before = Snapshot(fixture.Source);

        var result = fixture.Run("normal", "source-x");

        Assert.True(result.Exit == 0, result.Error);
        Assert.Equal(before, Snapshot(fixture.Source));
        var state = Path.Combine(fixture.Scratch("source-x"), ".orchestrator");
        Assert.Equal(["cohort-acceptance.db", "state.db"],
            Directory.EnumerateFiles(state).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(state, "older-backup")));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("source/.")]
    [InlineData("source/")]
    [InlineData("source/child")]
    [InlineData("source/child/grandchild")]
    [InlineData("existing")]
    public void ExistingOrContainedScratch_RefusesBeforeCreatingFiles(string name)
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        Directory.CreateDirectory(fixture.Scratch("existing"));
        var result = fixture.Run("normal", name);

        Assert.NotEqual(0, result.Exit);
        Assert.Contains("assertion=scratch-containment", result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Scratch("existing")));
    }

    [Fact]
    public void AliasIntoSource_RefusesBeforeCreatingScratch()
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        var link = fixture.Scratch("alias");
        if (OperatingSystem.IsWindows())
        {
            var junction = TestChildProcessCapture.Run(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe", WorkingDirectory = fixture.Source, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "/d", "/c", "mklink", "/J", link, fixture.Source }
            });
            Assert.Equal(0, junction.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, fixture.Source);
        }

        try
        {
            var result = fixture.Run("normal", Path.Combine("alias", "child"));
            Assert.NotEqual(0, result.Exit);
            Assert.Contains("assertion=scratch-containment", result.Error);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Source));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private static (string Path, long Length, DateTime Modified, string Hash)[] Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return (Path.GetRelativePath(directory, path), stream.Length, File.GetLastWriteTimeUtc(path),
                    Convert.ToHexString(SHA256.HashData(stream)));
            })
            .ToArray();
}
