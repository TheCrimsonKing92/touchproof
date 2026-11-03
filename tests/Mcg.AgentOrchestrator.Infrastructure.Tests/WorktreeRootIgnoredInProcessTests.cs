using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Serializes the actual GIT_DIR mutation; all repositories and launch windows are test-owned.
[Collection(TestCollections.CliProcessEnvironment)]
public sealed class WorktreeRootIgnoredInProcessTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("root-git-directory-").FullName;
    private readonly Action<ProcessStartInfo>? previousObserver = GitCli.StartInfoObserver;
    private readonly List<string[]> launches = [];

    public WorktreeRootIgnoredInProcessTests()
    {
        var environment = new Dictionary<string, string>();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE" })
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        environment["GIT_AUTHOR_NAME"] = environment["GIT_COMMITTER_NAME"] = "Root resolver";
        environment["GIT_AUTHOR_EMAIL"] = environment["GIT_COMMITTER_EMAIL"] = "resolver@example.test";
        environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_COUNT"] = "2";
        environment["GIT_CONFIG_KEY_0"] = "commit.gpgSign";
        environment["GIT_CONFIG_VALUE_0"] = "false";
        environment["GIT_CONFIG_KEY_1"] = "core.hooksPath";
        environment["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks");
        GitCli.StartInfoObserver = info =>
        {
            previousObserver?.Invoke(info);
            launches.Add(info.ArgumentList.ToArray());
            info.Environment.Clear();
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
            info.StandardOutputEncoding = Encoding.UTF8;
        };
    }

    [Fact]
    public void MainCheckout_ResolvesGitAnswerWithoutLaunchingGit()
    {
        var repo = CreateRepository();
        var expected = ReferenceDirectory(repo);
        Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".git")), expected);
        Assert.True(GitRevisionReader.TryConfirmWorkTree(repo));
        Assert.Equal(FileAttributes.Directory,
            File.GetAttributes(expected) & (FileAttributes.Directory | FileAttributes.ReparsePoint));

        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(WorktreeRootIgnoredInProcessTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        var before = launches.Count;
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out var actual));
        var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());

        Assert.Equal(expected, actual);
        Assert.True(Path.IsPathFullyQualified(actual));
        Assert.Equal(0, delta.Launches);
        Assert.Equal(before, launches.Count);
    }

    [Fact]
    public void NestedDirectory_DeclinesDespiteConfirmedWorktree()
    {
        var repo = CreateRepository();
        var nested = Directory.CreateDirectory(Path.Combine(repo, "nested", "child")).FullName;
        Assert.True(GitRevisionReader.TryConfirmWorkTree(nested));
        Assert.Equal(Path.Combine(repo, ".git"), ReferenceDirectory(nested));
        AssertDeclines(nested);
    }

    [Fact]
    public void LinkedWorktree_DeclinesGitdirFile()
    {
        var repo = CreateRepository();
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "-b", "linked", linked);
        Assert.StartsWith("gitdir: ", File.ReadAllText(Path.Combine(linked, ".git")));
        Assert.True(GitRevisionReader.TryConfirmWorkTree(linked));
        Assert.True(Directory.Exists(ReferenceDirectory(linked)));
        AssertDeclines(linked);
    }

    [Fact]
    public void BareRepository_DeclinesDespiteUsableGitDirectory()
    {
        var bare = Directory.CreateDirectory(Path.Combine(root, "bare")).FullName;
        Git(bare, "init", "--bare");
        Assert.Equal(bare, ReferenceDirectory(bare));
        AssertDeclines(bare);
    }

    [Fact]
    public void PlainDirectory_DeclinesAndGitHasNoAnswer()
    {
        var plain = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
        var reference = GitCli.Run(plain, "rev-parse", "--git-dir");
        Assert.False(reference.Succeeded);
        Assert.True(string.IsNullOrWhiteSpace(reference.Output));
        AssertDeclines(plain);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnfamiliarMetadata_DeclinesFreshly(bool unexpectedHead)
    {
        var repo = CreateRepository();
        var expected = ReferenceDirectory(repo);
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out _));
        if (unexpectedHead)
            File.WriteAllText(Path.Combine(expected, "HEAD"), "unknown\n");
        else
            File.AppendAllText(Path.Combine(expected, "config"), "\n[core]\n unknown = true\n");

        var reference = GitCli.Run(repo, "rev-parse", "--git-dir");
        if (reference.Succeeded && !string.IsNullOrWhiteSpace(reference.Output))
            Assert.Equal(expected, ResolveAnswer(repo, reference.Output));
        else
            Assert.True(string.IsNullOrWhiteSpace(reference.Output));
        AssertDeclines(repo);
    }

    [Fact]
    public void GitDirEnvironmentOverride_DeclinesAndRestoresVariable()
    {
        var repo = CreateRepository();
        var expected = ReferenceDirectory(repo);
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out _));
        var previous = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", expected);
            AssertDeclines(repo);
        }
        finally { Environment.SetEnvironmentVariable("GIT_DIR", previous); }
        Assert.Equal(previous, Environment.GetEnvironmentVariable("GIT_DIR"));
    }

    [Fact]
    public void InvalidInputsAndLookupExceptions_DeclineWithoutThrowing()
    {
        var repo = CreateRepository();
        AssertDeclines(null!);
        AssertDeclines("\0");
        AssertDeclines(Path.Combine(repo, "missing"));
        Assert.False(GitRevisionReader.TryResolveRootGitDirectory(repo, out var nullLookup, null!));
        Assert.Equal(string.Empty, nullLookup);
        Assert.False(GitRevisionReader.TryResolveRootGitDirectory(repo, out var throwingLookup,
            _ => throw new IOException("lookup unavailable")));
        Assert.Equal(string.Empty, throwingLookup);
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out var actual, _ => null));
        Assert.Equal(ReferenceDirectory(repo), actual);
    }

    [Fact]
    public void Ensure_MainCheckoutSkipsProbeAndKeepsExcludeBytesOnRepeatedCalls()
    {
        var repo = CreateRepository();
        var exclude = Path.Combine(ReferenceDirectory(repo), "info", "exclude");
        Assert.True(GitRevisionReader.TryResolveRootGitDirectory(repo, out _));
        var goal = GoalId.New();
        launches.Clear();

        var worktree = GoalWorktrees.Ensure(repo, goal);
        Assert.True(Directory.Exists(worktree));
        Assert.NotEmpty(launches);
        Assert.Equal(0, GitDirectoryProbes());
        Assert.Single(File.ReadAllLines(exclude), line => line == ".orchestrator-worktrees/");
        var bytes = File.ReadAllBytes(exclude);

        Assert.Equal(worktree, GoalWorktrees.Ensure(repo, goal));
        Assert.Equal(bytes, File.ReadAllBytes(exclude));
        Assert.True(Directory.Exists(GoalWorktrees.Ensure(repo, GoalId.New())));
        Assert.Equal(bytes, File.ReadAllBytes(exclude));
        Assert.Equal(0, GitDirectoryProbes());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ensure_PreservesSeparatorAndFallbackProbe(bool nested)
    {
        var repo = CreateRepository();
        var gitDirectory = ReferenceDirectory(repo);
        var directory = nested ? Directory.CreateDirectory(Path.Combine(repo, "nested")).FullName : repo;
        Assert.Equal(gitDirectory, ReferenceDirectory(directory));
        Assert.Equal(!nested, GitRevisionReader.TryResolveRootGitDirectory(directory, out _));
        var exclude = Path.Combine(gitDirectory, "info", "exclude");
        const string original = "# fixture without trailing newline";
        File.WriteAllText(exclude, original, new UTF8Encoding(false));
        launches.Clear();

        Assert.True(Directory.Exists(GoalWorktrees.Ensure(directory, GoalId.New())));

        var expected = original + Environment.NewLine + ".orchestrator-worktrees/" + Environment.NewLine;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(exclude));
        Assert.Equal(nested ? 1 : 0, GitDirectoryProbes());
    }

    private int GitDirectoryProbes() => launches.Count(args =>
        args.TakeLast(2).SequenceEqual(new[] { "rev-parse", "--git-dir" }));

    private static void AssertDeclines(string directory)
    {
        Assert.False(GitRevisionReader.TryResolveRootGitDirectory(directory, out var actual));
        Assert.Equal(string.Empty, actual);
    }

    private static string ReferenceDirectory(string directory)
    {
        var result = GitCli.Run(directory, "rev-parse", "--git-dir");
        Assert.True(result.Succeeded, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
        return ResolveAnswer(directory, result.Output);
    }

    private static string ResolveAnswer(string directory, string output)
    {
        var gitDirectory = output.Trim();
        return Path.IsPathRooted(gitDirectory)
            ? gitDirectory : Path.GetFullPath(Path.Combine(directory, gitDirectory));
    }

    private string CreateRepository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Git(repo, "init", "--initial-branch=fixture-main");
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private static void Git(string directory, params string[] args)
    {
        var result = GitCli.Run(directory, args);
        Assert.True(result.Succeeded, result.Error);
    }

    public void Dispose()
    {
        GitCli.StartInfoObserver = previousObserver;
        SharedTestSupport.RemoveTempDirectory(root);
    }
}
