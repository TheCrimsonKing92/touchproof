using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: test-owned runners and providers, no real git, gh, credentials or HTTP.
public sealed class GitHubPullRequestPublisherForgeIdentityTests
{
    private const string Grant = "opaque-installation-grant";
    private const string Candidate = "1111111111111111111111111111111111111111";
    private static readonly string Basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + Grant));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_ConfiguredIdentity_PassesEnvironmentAndNamesActor(bool existing)
    {
        var runner = new Runner { Existing = existing };
        var provider = new Provider();
        var publisher = new GitHubPullRequestPublisher(runner, provider);

        var result = publisher.Publish(Request() with { ForgeIdentityPath = "settings.json" });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("mcg-orchestrator[bot]", result.ActingLogin);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("settings.json", provider.Path);
        Assert.Equal(existing ? 4 : 5, runner.Calls.Count);
        Assert.Equal("git push", runner.Calls[2].Command);
        Assert.Equal("gh pr list", runner.Calls[3].Command);
        if (!existing) Assert.Equal("gh pr create", runner.Calls[4].Command);
        Assert.All(runner.Calls, call =>
        {
            Assert.Equal(8, call.Environment.Count);
            Assert.Equal(Grant, call.Environment["GH_TOKEN"]);
            Assert.Equal("AUTHORIZATION: basic " + Basic, call.Environment["GIT_CONFIG_VALUE_1"]);
            Assert.Equal("", call.Environment["GIT_CONFIG_VALUE_0"]);
            Assert.Equal("0", call.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.DoesNotContain(Grant, string.Join(" ", call.Arguments));
            Assert.DoesNotContain(Basic, string.Join(" ", call.Arguments));
        });
        Assert.DoesNotContain(Grant, result.ToString());
        Assert.DoesNotContain(Basic, runner.Body ?? "");
        Assert.DoesNotContain(Grant, runner.Body ?? "");

        Assert.True(publisher.Publish(Request() with { ForgeIdentityPath = "settings.json" }).Succeeded);
        Assert.Equal(2, provider.Calls); // A publish never reuses a previous grant.
    }

    [Fact]
    public void Publish_UnsetIdentity_PreservesExactEnvironmentAndSkipsProvider()
    {
        var runner = new Runner();
        var provider = new Provider { Error = new InvalidOperationException("must not acquire") };
        var request = Request();
        var result = new GitHubPullRequestPublisher(runner, provider).Publish(request);
        Assert.True(result.Succeeded);
        Assert.Null(result.ActingLogin);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(5, runner.Calls.Count);
        Assert.All(runner.Calls, call =>
        {
            Assert.Equal(2, call.Environment.Count);
            Assert.Equal("0", call.Environment["GIT_TERMINAL_PROMPT"]);
            Assert.Equal(Directory.GetParent(Path.GetFullPath(request.RepositoryRoot))!.FullName,
                call.Environment["GIT_CEILING_DIRECTORIES"]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_AcquisitionFailure_MakesNoForgeCalls(bool typed)
    {
        var runner = new Runner();
        var provider = new Provider { Error = typed
            ? new ForgeIdentityUnavailableException("exchange failed: ghs_sensitiveGrant")
            : new InvalidOperationException(Grant) };
        var result = new GitHubPullRequestPublisher(runner, provider).Publish(
            Request() with { ForgeIdentityPath = "configured" });
        Assert.False(result.Succeeded);
        Assert.Equal(PullRequestPublishFailureKind.ForgeIdentityUnavailable, result.FailureKind);
        Assert.Equal("forge-identity-unavailable", result.FailureToken);
        Assert.Null(result.PushedCommit);
        Assert.Equal(1, provider.Calls);
        Assert.Empty(runner.Calls);
        Assert.DoesNotContain("ghs_sensitiveGrant", result.Message ?? "");
        Assert.DoesNotContain(Grant, result.Message ?? "");
    }

    [Fact]
    public void Publish_FailedPush_RedactsBasicHeaderAndOpaqueGrant()
    {
        var runner = new Runner { PushError = "AUTHORIZATION: basic " + Basic + " raw=" + Grant };
        var result = new GitHubPullRequestPublisher(runner, new Provider()).Publish(
            Request() with { ForgeIdentityPath = "configured" });
        Assert.Equal(PullRequestPublishFailureKind.PushRejected, result.FailureKind);
        Assert.Equal("mcg-orchestrator[bot]", result.ActingLogin);
        Assert.Contains("[redacted]", result.Message ?? "");
        Assert.DoesNotContain(Basic, result.Message ?? "");
        Assert.DoesNotContain(Grant, result.Message ?? "");
        Assert.DoesNotContain(Basic, ForgeDiagnosticRedactor.Redact("AUTHORIZATION: basic " + Basic));
        Assert.DoesNotContain("ghs_leaked", ForgeDiagnosticRedactor.Redact("ghs_leaked"));
    }

    [Theory]
    [InlineData("git@github.com:octo/sample.git")]
    [InlineData("https://owner:password@github.com/octo/sample.git")]
    public void Publish_NonIdentityOrigin_RefusesBeforePush(string origin)
    {
        var runner = new Runner { Origin = origin };
        var result = new GitHubPullRequestPublisher(runner, new Provider()).Publish(
            Request() with { ForgeIdentityPath = "configured" });
        Assert.Equal(PullRequestPublishFailureKind.ForgeIdentityUnavailable, result.FailureKind);
        Assert.Single(runner.Calls);
    }

    private static PullRequestPublishRequest Request() => new(Path.Combine(Path.GetTempPath(), "publisher-repository"),
        "goal/12345678", Candidate, null, "main", "Title", "Body");

    private sealed class Provider : IForgeIdentityProvider
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public Exception? Error { get; init; }
        public ForgeIdentityPublish Acquire(string settingsPath)
        {
            Calls++;
            Path = settingsPath;
            if (Error is not null) throw Error;
            return new(new Dictionary<string, string>
            {
                ["GH_TOKEN"] = Grant, ["GIT_CONFIG_COUNT"] = "2",
                ["GIT_CONFIG_KEY_0"] = "http.https://github.com/.extraheader", ["GIT_CONFIG_VALUE_0"] = "",
                ["GIT_CONFIG_KEY_1"] = "http.https://github.com/.extraheader", ["GIT_CONFIG_VALUE_1"] = "AUTHORIZATION: basic " + Basic
            }, "mcg-orchestrator[bot]");
        }
    }

    private sealed class Runner : IForgeProcessRunner
    {
        public List<(string Command, string[] Arguments, IReadOnlyDictionary<string, string> Environment)> Calls { get; } = [];
        public bool Existing { get; init; }
        public string Origin { get; init; } = "https://github.com/octo/sample.git";
        public string? PushError { get; init; }
        public string? Body { get; private set; }
        public ForgeProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout)
        {
            var args = arguments.ToArray();
            Calls.Add((fileName + " " + string.Join(" ", args.Take(fileName == "git" ? 1 : 2)), args,
                new Dictionary<string, string>(environment)));
            if (fileName == "git") return args[0] switch
            {
                "remote" => Ok(Origin),
                "push" when PushError is not null => new(true, 1, "", PushError),
                _ => Ok("")
            };
            if (args[1] == "list") return Ok(Existing
                ? """[{"number":7,"url":"https://github.com/octo/sample/pull/7"}]""" : "[]");
            Body = File.ReadAllText(args[Array.IndexOf(args, "--body-file") + 1]);
            return Ok("https://github.com/octo/sample/pull/7");
        }
        private static ForgeProcessResult Ok(string text) => new(true, 0, text, "");
    }
}
