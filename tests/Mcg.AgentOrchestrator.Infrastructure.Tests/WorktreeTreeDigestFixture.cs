using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Synchronous, fixture-owned repositories and child environments; restores the thread-local hook.
internal sealed class WorktreeTreeDigestFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("digest-parity-").FullName;
    internal string Repo { get; set; }
    internal List<(string[] Arguments, string? OptionalLocks)> Launches { get; } = [];
    private readonly Dictionary<string, string> childEnvironment = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<ProcessStartInfo>? previousObserver = GitCli.StartInfoObserver;
    private IReadOnlyDictionary<string, string>? commandEnvironment;
    private bool computingDigest;

    internal WorktreeTreeDigestFixture()
    {
        Repo = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) childEnvironment[name] = value;
        childEnvironment["HOME"] = Root;
        childEnvironment["USERPROFILE"] = Root;
        childEnvironment["XDG_CONFIG_HOME"] = Root;
        childEnvironment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        childEnvironment["GIT_CONFIG_NOSYSTEM"] = "1";
        childEnvironment["GIT_AUTHOR_NAME"] = childEnvironment["GIT_COMMITTER_NAME"] = "Digest Fixture";
        childEnvironment["GIT_AUTHOR_EMAIL"] = childEnvironment["GIT_COMMITTER_EMAIL"] = "digest@example.test";
        GitCli.StartInfoObserver = Observe;
        try
        {
            Git("init", "--initial-branch=fixture-main", "--object-format=sha1", "--template=");
            File.AppendAllText(Path.Combine(Repo, ".git", "config"), "\n[core]\nfilemode = false\n");
            File.WriteAllText(Path.Combine(Repo, "tracked.txt"), "one\ntwo\n");
            File.WriteAllText(Path.Combine(Repo, ".gitignore"), "ignored/\n");
            File.WriteAllText(Path.Combine(Repo, ".gitattributes"), "*.txt text eol=lf\n");
            Git("add", "-A");
            Git("commit", "-m", "seed");
        }
        catch { Dispose(); throw; }
    }

    internal string? ReadEnvironment(string name) => childEnvironment.GetValueOrDefault(name);

    internal string Git(params string[] arguments) => GitWithEnvironment(null, arguments);

    private string GitWithEnvironment(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        commandEnvironment = environment;
        try
        {
            var result = GitCli.RunWithEnvironment(Repo, environment ?? childEnvironment,
                ["-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + Path.Combine(Root, "no-hooks"), .. arguments]);
            Assert.True(result.ProcessStarted);
            Assert.False(result.DrainTimedOut);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }
        finally { commandEnvironment = null; }
    }

    internal string Compute(Func<string, string?>? environment = null, bool defaultOverload = false)
    {
        Launches.Clear();
        computingDigest = true;
        try
        {
            string digest, reason;
            var succeeded = defaultOverload
                ? WorktreeTreeDigest.TryCompute(Repo, out digest, out reason)
                : WorktreeTreeDigest.TryCompute(Repo, out digest, out reason, out _, environment: environment ?? ReadEnvironment);
            Assert.True(succeeded, reason);
            Assert.Equal("matched", reason);
            return digest;
        }
        finally { computingDigest = false; }
    }

    // Independent copy of the original four-step algorithm, using real git and a separate index.
    internal string OriginalDigest()
    {
        var temporary = Directory.CreateDirectory(Path.Combine(Root, "oracle-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var objects = Git("rev-parse", "--path-format=absolute", "--git-path", "objects").Trim();
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temporary, "index"),
                ["GIT_OBJECT_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(temporary, "objects")).FullName,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objects,
                ["GIT_OPTIONAL_LOCKS"] = "0"
            };
            GitWithEnvironment(environment, "read-tree", "HEAD");
            GitWithEnvironment(environment, "add", "-A");
            var tree = GitWithEnvironment(environment, "write-tree").Trim();
            Assert.Equal(40, tree.Length);
            Assert.All(tree, character => Assert.True(Uri.IsHexDigit(character)));
            return "wbc1:git-tree:" + tree.ToLowerInvariant();
        }
        finally { SharedTestSupport.RemoveTempDirectory(temporary); }
    }

    private void Observe(ProcessStartInfo start)
    {
        var arguments = start.ArgumentList.Skip(GitCli.HardeningArguments.Count).ToArray();
        var overrides = new Dictionary<string, string>();
        if (computingDigest)
        {
            // Preserve only the explicit per-step environment supplied by the digest under test.
            var names = arguments[0] is "read-tree" or "add" or "write-tree"
                ? new[] { "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_OPTIONAL_LOCKS" }
                : arguments[0] == "status" ? new[] { "GIT_OPTIONAL_LOCKS" } : [];
            foreach (var name in names) overrides[name] = start.Environment[name]!;
        }
        start.Environment.Clear();
        foreach (var (name, value) in childEnvironment) start.Environment[name] = value;
        foreach (var (name, value) in commandEnvironment ?? overrides) start.Environment[name] = value;
        if (computingDigest)
            Launches.Add((arguments, start.Environment.TryGetValue("GIT_OPTIONAL_LOCKS", out var locks) ? locks : null));
    }

    public void Dispose()
    {
        GitCli.StartInfoObserver = previousObserver;
        SharedTestSupport.RemoveTempDirectory(Root);
    }
}
