using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliProcessEnvironmentWorkerBuildSeedTests
{
    [Fact]
    public void SeedCensusRunsInANonParallelCollection()
    {
        var membership = typeof(WorkerBuildSeedTests).GetCustomAttribute<CollectionAttribute>();
        var definition = typeof(CliProcessEnvironmentCollection).GetCustomAttribute<CollectionDefinitionAttribute>();
        Assert.NotNull(membership);
        Assert.NotNull(definition);
        Assert.Equal(definition.Name, membership.Name);
        Assert.True(definition.DisableParallelization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedMeasurementCountsLinkedCheckoutWithEitherObserverOrder(bool sharedFirst)
    {
        var original = Interlocked.Exchange(ref GitCli.LaunchObserver, null);
        try
        {
            if (sharedFirst)
                Assert.True(HostCapacitySlotGitCensus.EnsureInstalled(nameof(WorkerBuildSeedTests), ref GitCli.LaunchObserver));

            var prior = GitCli.LaunchObserver;
            var observedWorktreeLaunches = 0;
            Action<string> scoped = command =>
            {
                prior?.Invoke(command);
                if (command == "worktree") observedWorktreeLaunches++;
            };
            GitCli.LaunchObserver = scoped;

            var measurement = WorkerBuildSeedTests.MeasureFixture("worker-build observer ordering", () =>
                new WorkerBuildRequestFixture(linkedWorktree: true));
            using var fixture = measurement.Fixture;
            Assert.Equal(1, observedWorktreeLaunches);
            Assert.Equal(1L, measurement.Counts.Count("worktree"));
            Assert.Same(scoped, GitCli.LaunchObserver);
        }
        finally
        {
            Interlocked.Exchange(ref GitCli.LaunchObserver, original);
        }
    }

    [Fact]
    public void TimezoneOverrideUsesGitForCommitTimestamps()
    {
        var previous = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", "UTC0");
            var measurement = WorkerBuildSeedTests.MeasureFixture("worker-build Git TZ fallback", () =>
                new WorkerBuildRequestFixture());
            using var fixture = measurement.Fixture;
            WorkerBuildSeedTests.AssertSetupLaunches(measurement.Counts, 1);
            Assert.Equal("+0000", Git(fixture.RepositoryRoot, "show", "-s", "--format=%ai", "HEAD")[^5..]);
            Assert.Equal("+0000", Git(fixture.RepositoryRoot, "show", "-s", "--format=%ci", "HEAD")[^5..]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", previous);
        }
    }

    [Fact]
    public void OracleWritesAndReadsItsOwnRepositoryWithInheritedGitDir()
    {
        var parent = Directory.CreateTempSubdirectory("mcg-worker-build-oracle-");
        var previous = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            var oracle = Directory.CreateDirectory(Path.Combine(parent.FullName, "oracle")).FullName;
            var decoy = Directory.CreateDirectory(Path.Combine(parent.FullName, "decoy")).FullName;
            Git(decoy, "init", "-b", "decoy");
            var decoyMetadata = Path.Combine(decoy, ".git");
            Environment.SetEnvironmentVariable("GIT_DIR", decoyMetadata);

            var redirected = GitCli.RunDirect(oracle, "symbolic-ref", "--short", "HEAD");
            Assert.True(redirected.Succeeded, redirected.Error);
            Assert.Equal("decoy", redirected.Output.Trim());

            Git(oracle, "init", "-b", "main");
            Assert.True(Directory.Exists(Path.Combine(oracle, ".git")));
            Git(oracle, "config", "user.name", "Oracle");
            Git(oracle, "config", "user.email", "oracle@fixture.invalid");
            File.WriteAllText(Path.Combine(oracle, "seed.txt"), "oracle seed");
            Git(oracle, "add", "seed.txt");
            Git(oracle, "commit", "-m", "oracle seed");

            Assert.Equal("main", Git(oracle, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal("oracle@fixture.invalid", Git(oracle, "config", "--local", "user.email"));
            Assert.Equal("oracle seed", Git(oracle, "show", "HEAD:seed.txt"));
            Assert.Equal("decoy", Git(decoy, "symbolic-ref", "--short", "HEAD"));
            Assert.False(File.Exists(Path.Combine(decoyMetadata, "index")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_DIR", previous);
            SharedTestSupport.RemoveTempDirectory(parent.FullName);
        }
    }

    private static string Git(string root, params string[] arguments) =>
        WorkerBuildSeedTests.DirectGit(root, null, arguments);
}
