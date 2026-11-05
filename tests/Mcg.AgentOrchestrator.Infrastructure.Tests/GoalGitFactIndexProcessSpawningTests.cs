using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Serial: installs the process-wide launch observer; repositories are private to each case.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GoalGitFactIndexProcessSpawningTests
{
    [Xunit.Theory]
    [Xunit.InlineData("main", false)]
    [Xunit.InlineData("goal/integration", false)]
    [Xunit.InlineData("main", true)]
    public void ColdBuild_IntegrationRef_ReturnsOnlyGoalTipsInThreeLaunches(
        string integration, bool collidingTag)
    {
        using var repo = new Repository();
        repo.Run("branch", integration);
        if (collidingTag) repo.Run("tag", integration);
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            repo.Commit($"goal-{i}");
            repo.Run("branch", $"goal/{i}");
            tips[$"goal/{i}"] = repo.Run("rev-parse", "HEAD").Trim();
        }
        var expectedMain = repo.Run("rev-parse", $"refs/heads/{integration}").Trim();
        using var launches = new Launches();

        var index = GoalGitFactIndex.Build(repo.Root, integration);

        Assert.Equal(new[] { "for-each-ref", "for-each-ref", "worktree" }, launches.Commands);
        Assert.True(index.IsAvailable);
        Assert.Equal(expectedMain, index.MainSha);
        Assert.Equal(tips.OrderBy(t => t.Key), index.Snapshot.GoalBranchTips.OrderBy(t => t.Key));
        Assert.False(index.HasGoalBranch(integration));
    }

    [Xunit.Fact]
    public void ColdBuild_MissingIntegration_PreservesGoalTipsWithoutEvidence()
    {
        using var repo = new Repository();
        repo.Run("branch", "goal/present");
        repo.Run("branch", "main/child");
        var tip = repo.Run("rev-parse", "HEAD").Trim();
        using var launches = new Launches();

        var index = GoalGitFactIndex.Build(repo.Root, "main");

        Assert.Equal(new[] { "for-each-ref", "for-each-ref", "worktree" }, launches.Commands);
        Assert.Null(index.MainSha);
        Assert.False(index.IsAvailable);
        var branch = Assert.Single(index.Snapshot.GoalBranchTips);
        Assert.Equal("goal/present", branch.Key);
        Assert.Equal(tip, branch.Value);
    }

    [Xunit.Fact]
    public void Build_CachedSnapshot_LaunchesNoGit()
    {
        using var repo = new Repository();
        repo.Run("branch", "main");
        repo.Run("branch", "goal/present");
        var cache = new GoalGitFactSnapshotCache();
        var first = GoalGitFactIndex.Build(repo.Root, "main", snapshotCache: cache);
        using var launches = new Launches();

        var second = GoalGitFactIndex.Build(repo.Root, "main", snapshotCache: cache);

        Assert.True(second.IsAvailable);
        Assert.Empty(launches.Commands);
        Assert.Equal(first.MainSha, second.MainSha);
        Assert.Equal(first.Snapshot.GoalBranchTips, second.Snapshot.GoalBranchTips);
    }

    private sealed class Launches : IDisposable
    {
        private readonly Action<string>? previous = GitCli.LaunchObserver;
        private readonly Action<string> observer;
        internal List<string> Commands { get; } = [];

        internal Launches()
        {
            var owner = Environment.CurrentManagedThreadId;
            observer = command =>
            {
                previous?.Invoke(command);
                if (Environment.CurrentManagedThreadId == owner) Commands.Add(command);
            };
            Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous));
        }

        public void Dispose() =>
            Assert.Same(observer, Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer));
    }

    internal sealed class Repository : IDisposable
    {
        private readonly Action<ProcessStartInfo>? previous = GitCli.StartInfoObserver;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-git-base-{Guid.NewGuid():N}");

        internal Repository()
        {
            Directory.CreateDirectory(Root);
            var environment = new Dictionary<string, string?>();
            foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "SystemDrive", "COMSPEC",
                         "PATHEXT", "TEMP", "TMP", "LOCALAPPDATA" })
                environment[key] = Environment.GetEnvironmentVariable(key);
            GitCli.StartInfoObserver = info =>
            {
                previous?.Invoke(info);
                info.Environment.Clear();
                foreach (var (key, value) in environment)
                    if (value is not null) info.Environment[key] = value;
                info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
                info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            };
            try
            {
                Run("init", "-b", "topic");
                Commit("seed");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal string Run(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result}");
            return result.Output;
        }

        internal void Commit(string name)
        {
            File.WriteAllText(Path.Combine(Root, name + ".txt"), name);
            Run("add", ".");
            Run("-c", "user.name=Tests", "-c", "user.email=tests@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "-m", name);
        }

        public void Dispose()
        {
            GitCli.StartInfoObserver = previous;
            GitRevisionReader.Shared.GetReader(Root)?.Terminate();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
    }
}
