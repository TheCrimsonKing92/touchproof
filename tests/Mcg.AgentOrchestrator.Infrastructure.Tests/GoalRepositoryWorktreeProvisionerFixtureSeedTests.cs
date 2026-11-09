using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;
using Xunit;
using Fixture = GoalRepositoryWorktreeProvisionerFixture;

// Parallel-safe: unique repositories and AsyncLocal-filtered, chained launch observation.
public sealed class GoalRepositoryWorktreeProvisionerFixtureSeedTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("trunk")]
    public void Constructor_WarmTemplate_CopiesIndependentSeedWithoutGit(string branch)
    {
        using var warm = new Fixture();
        var launches = new GitLaunches();
        var probes = InfrastructureTestSupport.CountGitProbeLaunches();
        Fixture measured;
        using (launches)
        using (probes)
        {
            measured = new Fixture(branch);
        }

        // Keep fixture cleanup and real-git verification outside the measured window.
        using (measured)
        {
            Assert.Empty(launches.Commands);
            Assert.Equal(0, probes.Count);
            var path = measured.CheckoutPath;
            Assert.Equal(branch, measured.IntegrationBranch);
            Assert.Equal(branch, RealGit(path, "symbolic-ref", "--short", "HEAD"));
            Assert.Equal("refs/heads/" + branch, RealGit(path, "for-each-ref", "--format=%(refname)", "refs/heads"));
            Assert.Equal("Worktree Provisioner Tests", RealGit(path, "config", "--local", "user.name"));
            Assert.Equal("provisioner@example.com", RealGit(path, "config", "--local", "user.email"));
            Assert.Equal("false", RealGit(path, "config", "--local", "commit.gpgsign"));
            Assert.Equal("false", RealGit(path, "config", "--local", "core.autocrlf"));
            Assert.Equal("1", RealGit(path, "rev-list", "--count", "HEAD"));
            Assert.Equal("Seed repository", RealGit(path, "log", "-1", "--format=%s"));
            Assert.Equal("A\texample.txt", RealGit(path, "show", "--name-status", "--format=", "HEAD"));
            Assert.Equal("seed\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(path, "example.txt")));
            Assert.Equal("seed", RealGit(path, "show", "HEAD:example.txt"));
            Assert.Equal("", RealGit(path, "status", "--porcelain"));

            var seed = RealGit(path, "rev-parse", "HEAD");
            Assert.Equal(seed, RealGit(warm.CheckoutPath, "rev-parse", "HEAD"));
            Assert.NotEqual(seed, Fixture.CommitChange(warm.CheckoutPath));
            Assert.Equal(seed, RealGit(path, "rev-parse", "HEAD"));
            Assert.Equal("seed\n", File.ReadAllText(Path.Combine(path, "example.txt")));
        }
    }

    internal static string RealGit(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut && result.ProcessStarted, result.Error);
        return result.Output.TrimEnd('\r', '\n');
    }

    internal sealed class GitLaunches : IDisposable
    {
        private readonly AsyncLocal<bool> active = new();
        private readonly ConcurrentQueue<string> commands = new();
        private readonly Action<string>? previous;
        private readonly Action<string> observer;

        internal string[] Commands => commands.ToArray();

        internal GitLaunches()
        {
            Action<string>? prior;
            Action<string> wrapper;
            do
            {
                prior = Volatile.Read(ref GitCli.LaunchObserver);
                var captured = prior;
                wrapper = command =>
                {
                    if (active.Value) commands.Enqueue(command);
                    captured?.Invoke(command);
                };
            } while (!ReferenceEquals(Interlocked.CompareExchange(ref GitCli.LaunchObserver, wrapper, prior), prior));
            previous = prior;
            observer = wrapper;
            active.Value = true;
        }

        public void Dispose()
        {
            active.Value = false;
            // Preserve another test's observer if it has stacked on this one.
            Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer);
        }
    }
}
