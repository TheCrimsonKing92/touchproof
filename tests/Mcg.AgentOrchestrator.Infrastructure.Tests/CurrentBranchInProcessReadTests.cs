using System.Reflection;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its repositories. Census windows use the current test
// identity; no process-wide observer or environment variable is replaced.
public sealed class CurrentBranchInProcessReadTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("current-branch-read-").FullName;
    private static readonly Func<string, string?> GetCurrentBranchName = typeof(GoalWorktrees)
        .GetMethod("GetCurrentBranchName", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)])!
        .CreateDelegate<Func<string, string?>>();

    [Fact]
    public void MainCheckout_MatchesGitWithoutLaunchingProcesses()
    {
        var repo = CreateRepository();
        var expected = Oracle(repo);
        Assert.Equal("fixture-main", expected);

        var launches = new LaunchRecording();
        Assert.True(GitRevisionReader.TryReadCurrentBranch(repo, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(expected, GetCurrentBranchName(repo));
        launches.AssertCommands();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedWorktree_AbsoluteOrRelativePointer_MatchesGit(bool relative)
    {
        var repo = CreateRepository();
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "-b", "linked", linked);
        var dotGit = Path.Combine(linked, ".git");
        Assert.True(File.Exists(dotGit));
        Assert.False(Directory.Exists(dotGit));
        var pointer = File.ReadAllText(dotGit);
        Assert.StartsWith("gitdir: ", pointer);
        Assert.True(Path.IsPathFullyQualified(pointer[8..].TrimEnd('\r', '\n')));
        if (relative)
        {
            var target = pointer[8..].TrimEnd('\r', '\n');
            // Git hides .git on Windows; open the existing file instead of recreating it.
            using var stream = File.Open(dotGit, FileMode.Open, FileAccess.Write);
            var bytes = Encoding.UTF8.GetBytes("gitdir: " + Path.GetRelativePath(linked, target).Replace('\\', '/') + "\r\n");
            stream.Write(bytes);
            stream.SetLength(stream.Position);
        }
        AssertRecognized(linked, "linked");
    }

    [Theory]
    [InlineData("sha1", false)]
    [InlineData("sha1", true)]
    [InlineData("sha256", false)]
    [InlineData("sha256", true)]
    public void DetachedHead_BothObjectFormatsAndHexCases_MatchesGit(string format, bool uppercase)
    {
        var repo = CreateRepository(format);
        Git(repo, "checkout", "--detach");
        var headPath = Path.Combine(repo, ".git", "HEAD");
        var objectId = File.ReadAllText(headPath).Trim();
        Assert.Equal(format == "sha1" ? 40 : 64, objectId.Length);
        if (uppercase) File.WriteAllText(headPath, objectId.ToUpperInvariant() + "\n");
        AssertRecognized(repo, null);
    }

    [Fact]
    public void UnbornBranch_DoesNotRequireAnExistingRefOrCommit()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "unborn")).FullName;
        Git(repo, "init", "--initial-branch=unborn-topic");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "unborn-topic")));
        AssertRecognized(repo, "unborn-topic");
    }

    [Theory]
    [InlineData("goal/abc")]
    [InlineData("goal/日本語")]
    public void BranchName_PreservesSlashesAndUnicode(string name)
    {
        var repo = CreateRepository();
        Git(repo, "checkout", "-b", name);
        // Git emits UTF-8, while redirected output defaults to the Windows console code page.
        // Pin only this synchronous test thread's launches and restore the previous observer.
        var previous = GitCli.StartInfoObserver;
        GitCli.StartInfoObserver = info =>
        {
            previous?.Invoke(info);
            info.StandardOutputEncoding = Encoding.UTF8;
        };
        try { AssertRecognized(repo, name); }
        finally { GitCli.StartInfoObserver = previous; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SymbolicHead_RecognizesOnlyTheOptionalTerminalNewline(string newline)
    {
        var repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/fixture-main" + newline);
        AssertRecognized(repo, "fixture-main");
    }

    [Fact]
    public void UnexpectedHead_UsesTheExistingGitFallback()
    {
        var repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref:refs/heads/fixture-main\n");
        Assert.Equal("fixture-main", Oracle(repo));
        AssertFallback(repo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown\n")]
    [InlineData("ref: refs/tags/topic\n")]
    [InlineData("ref: refs/heads/fixture-main\n\n")]
    [InlineData("ref: refs/heads/fixture-main\r")]
    [InlineData("ref: refs/heads/fixture-main \n")]
    [InlineData(" ref: refs/heads/fixture-main\n")]
    [InlineData("\uFEFFref: refs/heads/fixture-main\n")]
    [InlineData("ref: refs/heads/\n")]
    [InlineData("ref: refs/heads/.invalid\n")]
    [InlineData("ref: refs/heads/a..b\n")]
    [InlineData("ref: refs/heads/a//b\n")]
    [InlineData("ref: refs/heads/a.lock\n")]
    [InlineData("ref: refs/heads/a@{b\n")]
    [InlineData("ref: refs/heads/a?b\n")]
    [InlineData("ref: refs/heads/a\\b\n")]
    [InlineData("012345678901234567890123456789012345678g\n")]
    public void MalformedOrUnsupportedHead_FallsBackToGit(string head)
    {
        var repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), head);
        AssertFallback(repo);
    }

    [Theory]
    [InlineData("gitdir:")]
    [InlineData("gitdir: ")]
    [InlineData("gitdir: missing\n")]
    [InlineData("gitdir: missing\nextra\n")]
    [InlineData("\uFEFFgitdir: missing\n")]
    public void UnexpectedGitDirectoryPointer_FallsBackToGit(string pointer)
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "pointer")).FullName;
        Git(repo, "init", "--initial-branch=fixture-main");
        Directory.Move(Path.Combine(repo, ".git"), Path.Combine(root, "metadata"));
        File.WriteAllText(Path.Combine(repo, ".git"), pointer);
        AssertFallback(repo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolicRefChain_FallsBackInMainAndLinkedWorktrees(bool linked)
    {
        var repo = CreateRepository();
        var worktree = repo;
        var branch = "fixture-main";
        if (linked)
        {
            worktree = Path.Combine(root, "linked");
            branch = "linked";
            Git(repo, "worktree", "add", "-b", branch, worktree);
        }
        Git(repo, "branch", "destination");
        Git(repo, "symbolic-ref", "refs/heads/" + branch, "refs/heads/destination");
        Assert.Equal("destination", Oracle(worktree));
        AssertFallback(worktree);
    }

    [Fact]
    public void PackedBranchAndCheckoutChanges_AreReadFresh()
    {
        var repo = CreateRepository();
        Git(repo, "pack-refs", "--all");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "fixture-main")));
        AssertRecognized(repo, "fixture-main");
        Git(repo, "checkout", "-b", "next");
        AssertRecognized(repo, "next");
        Git(repo, "checkout", "--detach");
        AssertRecognized(repo, null);
    }

    [Fact]
    public void MissingInvalidUtf8AndOversizedHead_FallBackToGit()
    {
        var repo = CreateRepository();
        var headPath = Path.Combine(repo, ".git", "HEAD");
        File.Delete(headPath);
        AssertFallback(repo);
        File.WriteAllBytes(headPath, [0xff, 0xfe, 0xff]);
        AssertFallback(repo);
        File.WriteAllText(headPath, "ref: refs/heads/" + new string('x', 5000));
        AssertFallback(repo);
    }

    [Theory]
    [InlineData("GIT_DIR", "override")]
    [InlineData("GIT_WORK_TREE", "override")]
    [InlineData("GIT_COMMON_DIR", "override")]
    [InlineData("GIT_INDEX_FILE", "override")]
    [InlineData("GIT_OBJECT_DIRECTORY", "override")]
    [InlineData("GIT_ALTERNATE_OBJECT_DIRECTORIES", "override")]
    [InlineData("GIT_DIR", "")]
    public void EnvironmentOverride_DeclinesRead(string name, string value)
    {
        var repo = CreateRepository();
        Assert.True(GitRevisionReader.TryReadCurrentBranch(repo, out var baseline, _ => null));
        Assert.Equal("fixture-main", baseline);
        Assert.False(GitRevisionReader.TryReadCurrentBranch(repo, out var actual, key => key == name ? value : null));
        Assert.Null(actual);
    }

    [Theory]
    [InlineData("main", true, "fixture-main")]
    [InlineData("detached", true, null)]
    [InlineData("malformed", false, null)]
    public void AbsentEnvironment_MatchesRealLookup(string fixture, bool expected, string? expectedBranch)
    {
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            Assert.True(Environment.GetEnvironmentVariable(name) is null, $"{name} must be absent for lookup equivalence.");

        var repo = CreateRepository();
        if (fixture == "detached") Git(repo, "checkout", "--detach");
        if (fixture == "malformed") File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "unknown\n");

        var actual = GitRevisionReader.TryReadCurrentBranch(repo, out var branch, _ => null);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedBranch, branch);
        Assert.Equal(GitRevisionReader.TryReadCurrentBranch(repo, out var realBranch), actual);
        Assert.Equal(realBranch, branch);
    }

    [Fact]
    public void NullEnvironment_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => GitRevisionReader.TryReadCurrentBranch(root, out _, null!));
    }

    private static void AssertRecognized(string repo, string? expected)
    {
        Assert.Equal(expected, Oracle(repo));
        Assert.True(GitRevisionReader.TryReadCurrentBranch(repo, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(expected, GetCurrentBranchName(repo));
    }

    private static void AssertFallback(string repo)
    {
        var expected = Oracle(repo);
        Assert.False(GitRevisionReader.TryReadCurrentBranch(repo, out var actual));
        Assert.Null(actual);
        var launches = new LaunchRecording();
        Assert.Equal(expected, GetCurrentBranchName(repo));
        launches.AssertCommands("branch");
    }

    private static string? Oracle(string repo)
    {
        var result = GitCli.Run(repo, "branch", "--show-current");
        var branch = result.Output.Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : null;
    }

    private string CreateRepository(string format = "sha1")
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Git(repo, "init", "--initial-branch=fixture-main", "--object-format=" + format);
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private void Git(string repo, params string[] args)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Reader", ["GIT_AUTHOR_EMAIL"] = "reader@example.test",
            ["GIT_COMMITTER_NAME"] = "Reader", ["GIT_COMMITTER_EMAIL"] = "reader@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        };
        var result = GitCli.RunWithEnvironment(repo, environment, args);
        Assert.True(result.Succeeded, result.Error);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);

    private sealed class LaunchRecording
    {
        private readonly HostCapacitySlotGitCensus.Window window;

        internal LaunchRecording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(CurrentBranchInProcessReadTests));
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        }

        internal void AssertCommands(params string[] expected)
        {
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(expected.Length, delta.Launches);
            Assert.Equal(expected.GroupBy(command => command).ToDictionary(group => group.Key, group => group.Count())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal), delta.Subcommands.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }
    }
}
