using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Every repository is test-owned. The start observer is thread-local and the launch census
// is keyed by test identity, so these synchronous windows can run in parallel.
internal sealed class GitWorkspaceLaunchFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("git-launch-evidence-").FullName;

    internal string CreateRepository(string name = "repo")
    {
        var repo = Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        Git(repo, "init", "--initial-branch=main", "--template=");
        Git(repo, "-c", "user.name=Launch Tests", "-c", "user.email=launch@example.test",
            "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" + Path.Combine(Root, "no-hooks"),
            "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    internal string AddLinkedWorktree(string repo, string name = "linked")
    {
        var path = Path.Combine(repo, GoalWorktrees.DirectoryName, name);
        Git(repo, "worktree", "add", "--detach", path, "HEAD");
        return path;
    }

    internal static string Git(string directory, params string[] args)
    {
        var result = InfrastructureTestSupport.RunGitProbe(directory, args);
        Assert.True(result.Succeeded, result.ToString());
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        return result.StandardOutput.Trim();
    }

    internal static string CommonDirectory(string directory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        // The test probe deliberately strips repository variables. This oracle must use
        // the same effective overrides as the production fallback for the GIT_DIR case.
        if (environment is not null)
        {
            var overridden = GitCli.RunWithEnvironment(directory, environment, "rev-parse", "--git-common-dir");
            Assert.True(overridden.Succeeded, overridden.Error);
            return Path.GetFullPath(overridden.Output.Trim(), directory);
        }
        var result = InfrastructureTestSupport.RunGitProbe(directory,
            ["rev-parse", "--git-common-dir"]);
        Assert.True(result.Succeeded, result.ToString());
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        return Path.GetFullPath(result.StandardOutput.Trim(), directory);
    }

    internal static string AdminDirectory(string linked) => Path.GetFullPath(
        File.ReadAllText(Path.Combine(linked, ".git"))[8..].TrimEnd('\r', '\n'), linked);

    internal static string Normalize(string path) => OperatingSystem.IsWindows()
        ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);

    internal sealed class Recording : IDisposable
    {
        private readonly HostCapacitySlotGitCensus.Window window;
        private readonly Action<ProcessStartInfo>? previous;
        private bool closed;
        internal List<string[]> Arguments { get; } = [];

        internal Recording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            // Installs GitCli.LaunchObserver without replacing the gate's census observer.
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(GitWorkspaceLaunchFixture));
            Assert.NotNull(GitCli.LaunchObserver);
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
            previous = GitCli.StartInfoObserver;
            GitCli.StartInfoObserver = info =>
            {
                Arguments.Add(info.ArgumentList.Skip(GitCli.HardeningArguments.Count).ToArray());
                previous?.Invoke(info);
            };
        }

        internal int Close(params string[]? expectedCommands)
        {
            Assert.False(closed);
            closed = true;
            GitCli.StartInfoObserver = previous;
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(Arguments.Count, delta.Launches);
            if (expectedCommands is not null)
            {
                Assert.Equal(expectedCommands, Arguments.Select(args => args[0]));
                Assert.Equal(expectedCommands.GroupBy(command => command)
                    .ToDictionary(group => group.Key, group => group.Count()).OrderBy(pair => pair.Key),
                    delta.Subcommands.OrderBy(pair => pair.Key));
            }
            return delta.Launches;
        }

        public void Dispose()
        {
            if (closed) return;
            GitCli.StartInfoObserver = previous;
            _ = window.Close();
            closed = true;
        }
    }

    internal sealed class Labeler(string? failPath = null) : IWorkerIntegrityLabeler
    {
        internal List<(string Path, string Level, bool Recursive)> Calls { get; } = [];
        public IntegrityLabelState Query(string path) => new(Exists: true, Low: false, Inheritable: false, Medium: true);
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add((Normalize(path), level, recursive));
            return failPath is null || Normalize(path) != Normalize(failPath);
        }
    }
}
