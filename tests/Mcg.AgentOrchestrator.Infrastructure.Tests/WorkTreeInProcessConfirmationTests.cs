using System.Reflection;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Repositories and census windows belong to the current test; the observer is never replaced.
public sealed class WorkTreeInProcessConfirmationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("worktree-confirmation-").FullName;

    [Fact]
    public void MainCheckoutAndNestedDirectory_MatchGitWithZeroLaunches()
    {
        var repo = CreateRepository();
        AssertConfirmed(repo);
        AssertConfirmed(Directory.CreateDirectory(Path.Combine(repo, "nested", "child")).FullName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedWorktree_ResolvesAbsoluteAndRelativePointers(bool relative)
    {
        var repo = CreateRepository();
        var linked = Path.Combine(root, "linked");
        Git(repo, "worktree", "add", "-b", "linked", linked);
        var entry = Path.Combine(linked, ".git");
        Assert.True(File.Exists(entry));
        Assert.StartsWith("gitdir: ", File.ReadAllText(entry));
        if (relative)
        {
            var target = File.ReadAllText(entry)[8..].TrimEnd('\r', '\n');
            // Git hides .git on Windows; open the existing file instead of recreating it.
            using var stream = File.Open(entry, FileMode.Open, FileAccess.Write);
            var bytes = Encoding.UTF8.GetBytes("gitdir: " + Path.GetRelativePath(linked, target) + "\n");
            stream.Write(bytes);
            stream.SetLength(stream.Position);
        }
        AssertConfirmed(linked);
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public void DetachedHead_MatchesGit(string format)
    {
        var repo = CreateRepository(format);
        Git(repo, "checkout", "--detach");
        AssertConfirmed(repo);
    }

    [Fact]
    public void InsideGitDirectory_UsesProbeAndPreservesDiagnostic()
    {
        var repo = CreateRepository();
        var directory = Path.Combine(repo, ".git", "refs");
        var oracle = Oracle(directory);
        Assert.True(oracle.Succeeded, oracle.Error);
        Assert.Equal("false", oracle.Output.Trim());
        Assert.False(GitRevisionReader.TryConfirmWorkTree(directory));
        var launches = new LaunchRecording();
        var exception = Assert.Throws<InvalidOperationException>(() => Require(directory));
        launches.AssertCommands("rev-parse");
        Assert.Contains("exit=0", exception.Message);
        Assert.Contains("stdout='false'", exception.Message);
    }

    [Fact]
    public void PlainDirectory_UsesProbeAndPreservesDiagnostic()
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
        var oracle = Oracle(directory);
        Assert.Equal(128, oracle.ExitCode);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(directory));
        var launches = new LaunchRecording();
        var exception = Assert.Throws<InvalidOperationException>(() => Require(directory));
        launches.AssertCommands("rev-parse");
        Assert.StartsWith($"Goal workspaces require '{directory}' to be inside a git work tree. ", exception.Message);
        Assert.Contains("probe='git rev-parse --is-inside-work-tree'", exception.Message);
        Assert.Contains("exit=128", exception.Message);
        Assert.Contains("processStarted=True", exception.Message);
        Assert.Contains("drainTimedOut=False", exception.Message);
        Assert.Contains("stdoutBytes=0; stdout=''", exception.Message);
        Assert.Contains("not a git repository", exception.Message);
        Assert.Contains("directoryExists=True", exception.Message);
    }

    [Fact]
    public void MissingGitdirTarget_DoesNotDiscoverEnclosingCheckout()
    {
        var repo = CreateRepository();
        var child = Directory.CreateDirectory(Path.Combine(repo, "child")).FullName;
        File.WriteAllText(Path.Combine(child, ".git"), "gitdir: missing\n");
        Assert.False(GitRevisionReader.TryConfirmWorkTree(child));
        Assert.False(Oracle(child).Succeeded);
    }

    [Theory]
    [InlineData("unknown\n")]
    [InlineData("ref:refs/heads/fixture-main\n")]
    [InlineData("ref: refs/heads/fixture-main\nextra\n")]
    [InlineData("ref: refs/heads/.invalid\n")]
    public void UnsupportedHead_FallsBackRegardlessOfGitOutcome(string head)
    {
        var repo = CreateRepository();
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), head);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        var oracle = Oracle(repo);
        var launches = new LaunchRecording();
        if (oracle.Succeeded && oracle.Output.Trim() == "true") Require(repo);
        else Assert.Throws<InvalidOperationException>(() => Require(repo));
        launches.AssertCommands("rev-parse");
    }

    [Theory]
    [InlineData("GIT_DIR", "override")]
    [InlineData("GIT_WORK_TREE", "override")]
    [InlineData("GIT_COMMON_DIR", "override")]
    [InlineData("GIT_CEILING_DIRECTORIES", "override")]
    [InlineData("GIT_DISCOVERY_ACROSS_FILESYSTEM", "override")]
    [InlineData("GIT_INDEX_FILE", "override")]
    [InlineData("GIT_OBJECT_DIRECTORY", "override")]
    [InlineData("GIT_ALTERNATE_OBJECT_DIRECTORIES", "override")]
    [InlineData("GIT_CONFIG_PARAMETERS", "override")]
    [InlineData("GIT_CONFIG_COUNT", "override")]
    [InlineData("GIT_DIR", "")]
    public void EnvironmentOverride_DeclinesConfirmation(string name, string value)
    {
        var repo = CreateRepository();
        Assert.True(GitRevisionReader.TryConfirmWorkTree(repo, _ => null));
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo, key => key == name ? value : null));
        AssertConfirmed(repo);
    }

    [Theory]
    [InlineData("repository", true)]
    [InlineData("plain", false)]
    [InlineData("unsupported-head", false)]
    public void AbsentEnvironment_MatchesRealLookup(string fixture, bool expected)
    {
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_CEILING_DIRECTORIES",
            "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
            "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" })
            Assert.True(Environment.GetEnvironmentVariable(name) is null, $"{name} must be absent for lookup equivalence.");

        var directory = fixture == "plain"
            ? Directory.CreateDirectory(Path.Combine(root, "plain")).FullName
            : CreateRepository();
        if (fixture == "unsupported-head")
            File.WriteAllText(Path.Combine(directory, ".git", "HEAD"), "unknown\n");

        var actual = GitRevisionReader.TryConfirmWorkTree(directory, _ => null);
        Assert.Equal(expected, actual);
        Assert.Equal(GitRevisionReader.TryConfirmWorkTree(directory), actual);
    }

    [Fact]
    public void NullEnvironment_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => GitRevisionReader.TryConfirmWorkTree(root, null!));
    }

    [Fact]
    public void BareRepositoryNestedInCheckout_StopsDiscovery()
    {
        var repo = CreateRepository();
        var bare = Directory.CreateDirectory(Path.Combine(repo, "bare")).FullName;
        Git(bare, "init", "--bare");
        var directory = Path.Combine(bare, "refs");
        Assert.False(GitRevisionReader.TryConfirmWorkTree(directory));
        var oracle = Oracle(directory);
        Assert.True(oracle.Succeeded, oracle.Error);
        Assert.Equal("false", oracle.Output.Trim());
    }

    [Theory]
    [InlineData("[core]\n bare = true\n")]
    [InlineData("[core]\n worktree = elsewhere\n")]
    [InlineData("[include]\n path = other.config\n")]
    [InlineData("[extensions]\n unknown = true\n")]
    [InlineData("[core]\n repositoryformatversion = 999\n")]
    [InlineData("[core]\n bare = nonsense\n")]
    [InlineData("[other]\n value = invalid\\escape\n")]
    [InlineData("[user]\n \u212Aey = x\n")]
    [InlineData("[\u212Aeys]\n value = x\n")]
    [InlineData("[core]\n repositoryformatversion = 1\n[extensions]\n objectformat = SHA1\n")]
    [InlineData("[core]\n repositoryformatversion = 0\n[extensions]\n objectformat = sha1\n")]
    [InlineData("[core]\n repositoryformatversion = 1\n[extensions]\n objectformat = sha1\n[core]\n repositoryformatversion = 0\n")]
    [InlineData("[user]\n \f name = x\n")]
    [InlineData("[user]\n name\u00A0 = x\n")]
    [InlineData("[user]\n name\r = x\n")]
    [InlineData("[core]\n bare = false\u00A0\n")]
    [InlineData("[core]\n filemode = nonsense\n")]
    [InlineData("[core]\n symlinks = nonsense\n")]
    [InlineData("[core]\n ignorecase = nonsense\n")]
    [InlineData("[core]\n logallrefupdates = nonsense\n")]
    [InlineData("[core]\n abbrev = nonsense\n")]
    [InlineData("[core]\n compression = nonsense\n")]
    [InlineData("[core]\n eol = nonsense\n")]
    [InlineData("[core]\n checkstat = nonsense\n")]
    [InlineData("[advice]\n detachedHead = nonsense\n")]
    [InlineData("[color]\n ui = nonsense\n")]
    [InlineData("[user]\n useconfigonly = nonsense\n")]
    [InlineData("[index]\n version = nonsense\n")]
    [InlineData("[pack]\n indexversion = nonsense\n")]
    [InlineData("[feature]\n manyfiles = nonsense\n")]
    [InlineData("[fetch]\n negotiationalgorithm = nonsense\n")]
    [InlineData("[core]\n unknown = true\n")]
    [InlineData("[other]\n unknown = true\n")]
    public void ConfigurationUncertainty_IsReadFreshAndUsesProbe(string config)
    {
        var repo = CreateRepository();
        AssertConfirmed(repo);
        File.AppendAllText(Path.Combine(repo, ".git", "config"), config);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        var oracle = Oracle(repo);
        var launches = new LaunchRecording();
        if (oracle.Succeeded && oracle.Output.Trim() == "true") Require(repo);
        else Assert.Throws<InvalidOperationException>(() => Require(repo));
        launches.AssertCommands("rev-parse");
    }

    [Theory]
    [InlineData("[CoRe]\n BaRe = FaLsE\n[ExTeNsIoNs]\n WoRkTrEeCoNfIg = FaLsE\n")]
    [InlineData("\t[core]\r\n\tbare\t=\tfalse\t# comment\r\n")]
    [InlineData("[core]\n repositoryformatversion = 1\n[extensions]\n objectformat = sha1\n")]
    [InlineData("[extensions]\n objectformat = sha1\n[core]\n repositoryformatversion = 0\n repositoryformatversion = 1\n")]
    [InlineData("[core]\n filemode = true\n symlinks = false\n ignorecase = true\n logallrefupdates = always\n")]
    [InlineData("[core]\n filemode = YES\n symlinks = No\n ignorecase = ON\n logallrefupdates = OFF\n")]
    [InlineData("[core]\n filemode = 1\n symlinks = 0\n ignorecase =\n logallrefupdates = AlWaYs\n")]
    public void ConfigurationSupportedSyntax_ConfirmsWithoutProbe(string config)
    {
        var repo = CreateRepository();
        File.AppendAllText(Path.Combine(repo, ".git", "config"), config);
        AssertConfirmed(repo);
    }

    [Fact]
    public void MissingFileAndInvalidInputs_NeverConfirmOrThrow()
    {
        var repo = CreateRepository();
        var file = Path.Combine(repo, "file");
        File.WriteAllText(file, "fixture");
        foreach (var path in new[] { Path.Combine(repo, "missing"), file, "\0", null! })
            Assert.False(GitRevisionReader.TryConfirmWorkTree(path));
    }

    [Fact]
    public void MissingInvalidUtf8AndOversizedMetadata_DeclineConfirmation()
    {
        var repo = CreateRepository();
        var headPath = Path.Combine(repo, ".git", "HEAD");
        var originalHead = File.ReadAllBytes(headPath);
        File.Delete(headPath);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        File.WriteAllBytes(headPath, [0xff, 0xfe, 0xff]);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        File.WriteAllText(headPath, "ref: refs/heads/" + new string('x', 5000));
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        File.WriteAllBytes(headPath, originalHead);
        var configPath = Path.Combine(repo, ".git", "config");
        var originalConfig = File.ReadAllBytes(configPath);
        File.WriteAllBytes(configPath, [0xff, 0xfe, 0xff]);
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        File.WriteAllText(configPath, "#" + new string('x', 65536));
        Assert.False(GitRevisionReader.TryConfirmWorkTree(repo));
        File.WriteAllBytes(configPath, originalConfig);
        AssertConfirmed(repo);
    }

    private static void AssertConfirmed(string directory)
    {
        var oracle = Oracle(directory);
        Assert.True(oracle.Succeeded, oracle.Error);
        Assert.Equal("true", oracle.Output.Trim());
        var launches = new LaunchRecording();
        Assert.True(GitRevisionReader.TryConfirmWorkTree(directory));
        Require(directory);
        launches.AssertCommands();
    }

    private static GitCli.GitResult Oracle(string directory) =>
        GitCli.Run(directory, "rev-parse", "--is-inside-work-tree");

    private static void Require(string directory) => typeof(GoalWorktrees)
        .GetMethod("RequireGitWorkTree", BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)])!
        .CreateDelegate<Action<string>>()(directory);

    private string CreateRepository(string format = "sha1")
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Git(repo, "init", "--initial-branch=fixture-main", "--object-format=" + format);
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private void Git(string directory, params string[] args)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Confirmation", ["GIT_AUTHOR_EMAIL"] = "confirmation@example.test",
            ["GIT_COMMITTER_NAME"] = "Confirmation", ["GIT_COMMITTER_EMAIL"] = "confirmation@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        };
        var result = GitCli.RunWithEnvironment(directory, environment, args);
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
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(WorkTreeInProcessConfirmationTests));
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
