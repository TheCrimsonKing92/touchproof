using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every cold-cache assertion creates a unique HEAD tree; no cache reset or ambient mutation.
public sealed class WorktreeDirtyTreeLaunchBudgetTests
{
    [Fact]
    public void TryCompute_ModelledDirtyShape_ColdThenWarmCache_UsesStatusAndOneLsTree()
    {
        using var fixture = new WorktreeTreeDigestFixture();
        MakeUniqueHead(fixture);
        File.WriteAllText(Path.Combine(fixture.Repo, "tracked.txt"), "first modification\n");
        string? temporary = null;
        var observations = 0;
        var expected = fixture.OriginalDigest();
        Assert.Equal(expected, fixture.Compute(temporaryRootObserver: path => temporary = path,
            launchObserver: start =>
            {
                Assert.NotNull(temporary);
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temporary, "objects")));
                Assert.False(start.Environment.ContainsKey("GIT_INDEX_FILE"));
                Assert.False(start.Environment.ContainsKey("GIT_OBJECT_DIRECTORY"));
                observations++;
            }));
        Assert.Equal(2, observations);
        Assert.Equal(["status", "ls-tree"], fixture.Launches.Select(launch => launch.Arguments[0]));
        Assert.All(fixture.Launches, launch => Assert.Equal("0", launch.OptionalLocks));
        Assert.NotNull(temporary);
        Assert.False(Directory.Exists(temporary));

        File.WriteAllText(Path.Combine(fixture.Repo, "tracked.txt"), "second modification\n");
        expected = fixture.OriginalDigest();
        observations = 0;
        temporary = null;
        Assert.Equal(expected, fixture.Compute(temporaryRootObserver: path => temporary = path,
            launchObserver: start =>
            {
                Assert.NotNull(temporary);
                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(temporary, "objects")));
                Assert.False(start.Environment.ContainsKey("GIT_INDEX_FILE"));
                Assert.False(start.Environment.ContainsKey("GIT_OBJECT_DIRECTORY"));
                observations++;
            }));
        Assert.Equal(1, observations);
        Assert.NotNull(temporary);
        Assert.False(Directory.Exists(temporary));
        Assert.Equal(["status"], fixture.Launches.Select(launch => launch.Arguments[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryCompute_TenModifiedFilesAtOneHeadTree_AveragesAtMostOneAndHalfLaunches(bool gateHermetic)
    {
        using var fixture = new WorktreeTreeDigestFixture(gateHermetic ? AcceptanceHermeticGitConfiguration.GateConfig : null);
        if (gateHermetic)
            File.WriteAllText(Path.Combine(fixture.Repo, ".gitattributes"), "* text=auto eol=lf\n");
        for (var i = 0; i < 10; i++) File.WriteAllText(Path.Combine(fixture.Repo, $"file-{i}.txt"), "seed\n");
        MakeUniqueHead(fixture);
        var total = 0;
        for (var i = 0; i < 10; i++)
        {
            if (i > 0) fixture.Git("checkout", "--", $"file-{i - 1}.txt");
            File.WriteAllText(Path.Combine(fixture.Repo, $"file-{i}.txt"), "modified\n");
            var expected = fixture.OriginalDigest();
            Assert.Equal(expected, fixture.Compute());
            Assert.Equal("status", fixture.Launches[0].Arguments[0]);
            Assert.All(fixture.Launches.Skip(1), launch => Assert.Equal("ls-tree", launch.Arguments[0]));
            total += fixture.Launches.Count;
        }
        Assert.InRange(total, 10, 15);
    }

    private static void MakeUniqueHead(WorktreeTreeDigestFixture fixture)
    {
        File.WriteAllText(Path.Combine(fixture.Repo, "cache-identity"), Guid.NewGuid().ToString("N") + "\n");
        fixture.Git("add", "-A");
        fixture.Git("commit", "-m", "unique tree");
    }
}
