using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its runner and never invokes git, gh or the network.
public sealed class GitHubPullRequestPublisherTests
{
    private const string Candidate = "1111111111111111111111111111111111111111";
    private const string Foreign = "2222222222222222222222222222222222222222";
    private const string Reference = "refs/heads/goal/12345678";
    private const string Url = "https://github.com/octo/sample/pull/7";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_branch_uses_absent_lease_and_temporary_body_is_deleted(bool createFails)
    {
        string? bodyPath = null;
        string? body = null;
        string[]? pushArguments = null;
        var ghArguments = new List<string[]>();
        var runner = new Runner((command, args) =>
        {
            if (command == "git" && args[0] == "remote") return Ok("https://github.com/octo/sample.git");
            if (command == "git" && args[0] == "ls-remote") return Ok("");
            if (command == "git" && args[0] == "push")
            {
                pushArguments = args;
                return Ok("");
            }
            ghArguments.Add(args);
            if (args[1] == "list") return Ok("[]");
            bodyPath = args[Array.IndexOf(args, "--body-file") + 1];
            body = File.ReadAllText(bodyPath);
            return createFails ? new(true, 1, "", "creation failed") : Ok(Url);
        });

        var result = new GitHubPullRequestPublisher(runner).Publish(Request());

        // Publisher exceptions become typed failures. Assert outside that boundary so
        // callback assertion failures cannot masquerade as an expected create failure.
        runner.AssertEnvironment();
        Assert.NotNull(pushArguments);
        Assert.Contains($"--force-with-lease={Reference}:", pushArguments);
        Assert.Contains($"{Candidate}:{Reference}", pushArguments);
        Assert.Equal(2, ghArguments.Count);
        Assert.All(ghArguments, args =>
        {
            Assert.Contains("octo/sample", args);
            Assert.Contains("release", args);
        });
        Assert.Equal("create", ghArguments[1][1]);
        Assert.Equal("approved body", body);
        Assert.NotNull(bodyPath);
        Assert.StartsWith(Path.GetFullPath(OrchestratorTempRoot.GetParent()) + Path.DirectorySeparatorChar,
            bodyPath, StringComparison.OrdinalIgnoreCase);
        if (createFails)
        {
            Assert.False(result.Succeeded);
            Assert.Equal(PullRequestPublishFailureKind.GhFailed, result.FailureKind);
            Assert.Equal("creation failed", result.Message);
        }
        else
        {
            Assert.True(result.Succeeded, $"{result.FailureToken}: {result.Message}");
            Assert.Equal(7, result.PullRequestNumber);
            Assert.Equal(Url, result.Url);
        }
        Assert.Equal(Candidate, result.PushedCommit);
        Assert.False(File.Exists(bodyPath));
        Assert.Equal(["git remote", "git ls-remote", "git push", "gh pr list", "gh pr create"], runner.Calls);
    }

    [Fact]
    public void Exact_candidate_retries_reuse_lowest_pr_without_push_or_create()
    {
        var runner = new Runner((command, args) => command == "git"
            ? args[0] == "remote" ? Ok("git@github.com:octo/sample.git") : Ok($"{Candidate}\t{Reference}")
            : Ok("""[{"number":9,"url":"https://github.com/octo/sample/pull/9"},{"number":7,"url":"https://github.com/octo/sample/pull/7"}]"""));

        var result = new GitHubPullRequestPublisher(runner).Publish(Request());

        runner.AssertEnvironment();
        Assert.True(result.Succeeded);
        Assert.Equal(7, result.PullRequestNumber);
        Assert.Equal(Url, result.Url);
        Assert.Equal(["git remote", "git ls-remote", "gh pr list"], runner.Calls);
    }

    [Fact]
    public void Foreign_remote_tip_never_authorizes_a_push()
    {
        var runner = new Runner((_, args) => args[0] == "remote"
            ? Ok("git@github.com:octo/sample.git") : Ok($"{Foreign}\t{Reference}"));
        var result = new GitHubPullRequestPublisher(runner).Publish(Request());
        runner.AssertEnvironment();
        Assert.Equal(PullRequestPublishFailureKind.PushRejected, result.FailureKind);
        Assert.Null(result.PushedCommit);
        Assert.Equal(["git remote", "git ls-remote"], runner.Calls);
    }

    [Theory]
    [InlineData("origin", PullRequestPublishFailureKind.NoGitHubOrigin)]
    [InlineData("push", PullRequestPublishFailureKind.PushRejected)]
    [InlineData("auth", PullRequestPublishFailureKind.GhNotSignedIn)]
    [InlineData("create", PullRequestPublishFailureKind.GhFailed)]
    public void Failures_are_typed_and_credentials_are_redacted(string stage, PullRequestPublishFailureKind expected)
    {
        const string secret = "ghp_doNotRetainThisCredential";
        var runner = new Runner((command, args) =>
        {
            if (command == "git" && args[0] == "remote") return Ok(stage == "origin" ? "https://gitlab.com/a/b" : "https://github.com/octo/sample.git");
            if (command == "git" && args[0] == "ls-remote") return Ok("");
            if (command == "git") return stage == "push" ? new(true, 1, "", secret) : Ok("");
            if (stage == "auth") return new(true, 4, "", "gh auth login " + secret);
            return args[1] == "list" ? Ok("[]") : new(true, 1, "", "failed " + secret);
        });
        var result = new GitHubPullRequestPublisher(runner).Publish(Request());
        runner.AssertEnvironment();
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureKind);
        Assert.DoesNotContain(secret, result.Message ?? "");
        if (stage is "auth" or "create") Assert.Equal("goal/12345678", result.PushedBranch);
    }

    private static PullRequestPublishRequest Request() => new(Path.Combine(Path.GetTempPath(), "publisher-repository"),
        "goal/12345678", Candidate, null, "release", "Title", "approved body");
    private static ForgeProcessResult Ok(string stdout) => new(true, 0, stdout, "");

    private sealed class Runner(Func<string, string[], ForgeProcessResult> run) : IForgeProcessRunner
    {
        public List<string> Calls { get; } = [];
        private readonly List<(string WorkingDirectory, IReadOnlyDictionary<string, string> Environment)> environments = [];

        public void AssertEnvironment()
        {
            Assert.NotEmpty(environments);
            Assert.All(environments, call =>
            {
                Assert.Equal(Directory.GetParent(call.WorkingDirectory)!.FullName, call.Environment["GIT_CEILING_DIRECTORIES"]);
                Assert.DoesNotContain("GH_TOKEN", call.Environment.Keys);
            });
        }

        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            environments.Add((workingDirectory, new Dictionary<string, string>(environment)));
            Calls.Add(fileName + " " + string.Join(" ", arguments.Take(fileName == "git" ? 1 : 2)));
            return run(fileName, arguments.ToArray());
        }
    }
}
