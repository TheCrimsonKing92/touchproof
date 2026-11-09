using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each class owns its repository; xUnit serializes its rows and counters are AsyncLocal.
public sealed class GitRefFileResolverFixtureConfigParityTests(
    GitRefFileResolverFixtureConfigParityTests.Repository repository)
    : IClassFixture<GitRefFileResolverFixtureConfigParityTests.Repository>
{
    public static IEnumerable<object[]> AcceptedConfigurations()
    {
        foreach (var (section, key) in new[]
                 { ("core", "autocrlf"), ("core", "longpaths"), ("commit", "gpgsign") })
        foreach (var value in new[] { "true", "false", "yes", "no", "on", "off", "1", "0", "", "TRUE", "No" })
            yield return [$"[{section}]\n{key} = {value}\n"];
        yield return ["[core]\nautocrlf = input\n"];
        foreach (var value in new[] { "0", "6700", "007", "2147483647" })
            yield return [$"[gc]\nauto = {value}\n"];
        yield return ["[core]\nautocrlf = input # inline comment\n"];
        yield return ["[COMMIT]\n\tGpgSign\t=\tFaLsE\t; inline comment\n"];
    }

    [Theory]
    [MemberData(nameof(AcceptedConfigurations))]
    public void AcceptedConfig_ResolvesLooseAndPackedRefsLikeGit(string config)
    {
        repository.WithConfig(config, () =>
        {
            var expected = repository.Oracle();
            for (var i = 0; i < Repository.Revisions.Length; i++)
            {
                Assert.True(GitRefFileResolver.TryResolve(repository.Repo, Repository.Revisions[i], out var actual));
                Assert.Equal(expected[i], actual);
            }
        });
    }

    [Fact]
    public void AllFourKeys_ProbeAnswersWithoutGitLaunches()
    {
        repository.WithConfig("[core]\nautocrlf = input\nlongpaths = true\n[commit]\ngpgsign = false\n[gc]\nauto = 0\n", () =>
        {
            foreach (var revision in Repository.Revisions)
            {
                // Explicit overrides force the real-git oracle before opening the counter windows.
                var expected = RunGitProbe(repository.Repo, ["rev-parse", "--verify", revision],
                    commandEnvironment: new Dictionary<string, string>());
                Assert.True(expected.Succeeded, expected.ToString());
                using var launches = CountGitProbeLaunches();
                using var answers = CountGitProbeRefFileAnswers();

                var actual = RunGitProbe(repository.Repo, ["rev-parse", "--verify", revision]);

                Assert.True(actual.Succeeded, actual.ToString());
                Assert.Equal(expected.StandardOutput, actual.StandardOutput);
                Assert.Equal(expected.StandardError, actual.StandardError);
                Assert.Equal(expected.ExitCode, actual.ExitCode);
                Assert.Contains("resolved from ref files", actual.Command, StringComparison.Ordinal);
                Assert.Null(actual.ChildProcessId);
                Assert.Equal(0, launches.Count);
                Assert.Equal(1, answers.Count);
            }
        });
    }

    public sealed class Repository : IDisposable
    {
        private readonly GitRefFileResolverParityTests.Fixture fixture = new();
        internal static readonly string[] Revisions = ["HEAD", "refs/heads/fixture-main", "refs/heads/packed"];
        internal string Repo => fixture.Repo;

        public Repository()
        {
            fixture.Git("branch", "packed");
            fixture.Git("pack-refs", "--all");
            fixture.Git("commit", "--allow-empty", "-m", "loose branch advances");
            var metadata = Path.Combine(Repo, ".git");
            Assert.True(File.Exists(Path.Combine(metadata, "refs", "heads", "fixture-main")));
            Assert.False(File.Exists(Path.Combine(metadata, "refs", "heads", "packed")));
            var expected = Oracle();
            Assert.Equal(expected[0], expected[1]);
            Assert.NotEqual(expected[0], expected[2]);
            Assert.Contains(expected[2] + " refs/heads/packed", File.ReadAllText(Path.Combine(metadata, "packed-refs")));
        }

        internal string[] Oracle()
        {
            var result = GitCli.Run(Repo, ["rev-parse", .. Revisions]);
            Assert.True(result.Succeeded, result.Error);
            var ids = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(Revisions.Length, ids.Length);
            return ids;
        }

        internal void WithConfig(string config, Action assertions)
        {
            var path = Path.Combine(Repo, ".git", "config");
            var original = File.ReadAllBytes(path);
            try
            {
                File.AppendAllText(path, "\n" + config);
                assertions();
            }
            finally { File.WriteAllBytes(path, original); }
        }

        public void Dispose() => fixture.Dispose();
    }
}
