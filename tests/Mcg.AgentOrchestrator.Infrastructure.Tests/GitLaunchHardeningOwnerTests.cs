using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repository and reader; the launch hook is thread-local
// and restored synchronously. No process-wide observer or environment is replaced.
public sealed class GitLaunchHardeningOwnerTests : IDisposable
{
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void HardeningArguments_CurrentContract_IsOrderedAndImmutable()
    {
        Assert.Equal(new[]
        {
            "-c", "core.fsmonitor=false",
            "-c", "core.longpaths=true",
            "-c", "gc.auto=0",
            "-c", "maintenance.auto=false"
        }, GitCli.HardeningArguments);
        var collection = Assert.IsAssignableFrom<IList<string>>(GitCli.HardeningArguments);
        Assert.Throws<NotSupportedException>(() => collection.Add("--version"));
        Assert.Throws<NotSupportedException>(() => collection[0] = "--version");
    }

    [Fact]
    public void Digest_RealRunner_PrependsHardeningToEveryStep()
    {
        var repo = CreateRepository();
        var launches = CaptureLaunches(() =>
            Assert.True(WorktreeTreeDigest.TryCompute(repo, out _, out var reason), reason));

        Assert.Equal(4, launches.Count);
        AssertLaunch(launches[0], "rev-parse", "--path-format=absolute", "--git-path", "objects");
        AssertLaunch(launches[1], "read-tree", "HEAD");
        AssertLaunch(launches[2], "add", "-A");
        AssertLaunch(launches[3], "write-tree");
        Assert.Equal(Environment.GetEnvironmentVariable("GIT_OPTIONAL_LOCKS"), launches[0].OptionalLocks);
        Assert.All(launches.Skip(1), launch => Assert.Equal("0", launch.OptionalLocks));
    }

    [Fact]
    public void Candidate_DirtyRepository_PrependsHardeningOnceToEveryStep()
    {
        var repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, "candidate.txt"), "candidate");
        var head = Probe(repo, "rev-parse", "HEAD");
        string commit = "", tree = "";
        var launches = CaptureLaunches(() =>
            Assert.True(RemoteBuildCandidateCommit.TryCreate(repo, RemoteBuildCandidateCommit.RunGit,
                out commit, out tree, out var reason), reason));

        Assert.Equal(6, launches.Count);
        AssertLaunch(launches[0], "rev-parse", "HEAD");
        AssertLaunch(launches[1], "rev-parse", "HEAD^{tree}");
        AssertLaunch(launches[2], "read-tree", "HEAD");
        AssertLaunch(launches[3], "add", "-A");
        AssertLaunch(launches[4], "write-tree");
        AssertLaunch(launches[5], "commit-tree", tree, "-p", head, "-m", "mcg remote build candidate");
        Assert.All(launches, launch => Assert.Equal("0", launch.OptionalLocks));
        Assert.Equal(tree, Probe(repo, "rev-parse", commit + "^{tree}"));
    }

    [Fact]
    public void Reader_PersistentProcess_UsesHardeningAndDisablesOptionalLocks()
    {
        var repo = CreateRepository();
        var expected = Probe(repo, "rev-parse", "HEAD");
        using var reader = new GitRevisionReader(admissionReads: 0);
        var launches = CaptureLaunches(() =>
        {
            Assert.True(reader.TryResolve(repo, "HEAD^{commit}", out var actual));
            Assert.Equal(expected, actual);
        });

        Assert.NotNull(reader.GetReader(repo));
        var launch = Assert.Single(launches);
        AssertLaunch(launch, "--git-dir=" + Path.Combine(repo, ".git"), "cat-file", "--batch-check");
        Assert.Equal("0", launch.OptionalLocks);
    }

    [Fact]
    public void GitCli_CallerOptionalLocks_StillForcesZeroAfterEnvironment()
    {
        var repo = CreateRepository();
        var launches = CaptureLaunches(() =>
        {
            var result = GitCli.RunWithEnvironment(repo,
                new Dictionary<string, string> { ["GIT_OPTIONAL_LOCKS"] = "1" }, "status", "--porcelain");
            Assert.True(result.Succeeded, result.Error);
        });

        var launch = Assert.Single(launches);
        AssertLaunch(launch, "status", "--porcelain");
        Assert.Equal("0", launch.OptionalLocks);
    }

    [Fact]
    public void SourceScan_AllSourceFiles_OnlyGitCliDeclaresHardeningLiteral()
    {
        var repository = InfrastructureTestSupport.FindRepositoryRoot();
        var matches = EnumerateSourceFiles(Path.Combine(repository, "src"))
            .Where(path => File.ReadAllText(path).Contains("core.fsmonitor=false", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repository, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "src/Mcg.AgentOrchestrator.Execution/Processes/GitCli.cs" }, matches);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory)) yield return path;
        foreach (var child in Directory.EnumerateDirectories(directory)
                     .Where(path => !SourceGuardFileFilter.BuildOutputAndScratch.Excludes(path)))
            foreach (var path in EnumerateSourceFiles(child)) yield return path;
    }

    private static void AssertLaunch(Launch launch, params string[] command)
    {
        Assert.Equal(GitCli.HardeningArguments, launch.Arguments.Take(GitCli.HardeningArguments.Count));
        Assert.Equal(command, launch.Arguments.Skip(GitCli.HardeningArguments.Count));
        Assert.True(launch.CreateNoWindow);
        Assert.False(launch.UseShellExecute);
    }

    private static List<Launch> CaptureLaunches(Action action)
    {
        var launches = new List<Launch>();
        var previous = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info => launches.Add(new Launch(info.ArgumentList.ToArray(),
            info.Environment.TryGetValue("GIT_OPTIONAL_LOCKS", out var locks) ? locks : null,
            info.CreateNoWindow, info.UseShellExecute));
        try { action(); }
        finally { GitCli.StartInfoObserver = previous; }
        return launches;
    }

    private string CreateRepository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Probe(repo, "init", "--initial-branch=fixture-main");
        Probe(repo, "config", "user.name", "Hardening owner");
        Probe(repo, "config", "user.email", "hardening@example.test");
        Probe(repo, "config", "commit.gpgSign", "false");
        Probe(repo, "config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        Probe(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private static string Probe(string repo, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(repo, args);
        Assert.True(result.Succeeded, result.StandardError);
        return result.StandardOutput.Trim();
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);

    private sealed record Launch(string[] Arguments, string? OptionalLocks,
        bool CreateNoWindow, bool UseShellExecute);
}
