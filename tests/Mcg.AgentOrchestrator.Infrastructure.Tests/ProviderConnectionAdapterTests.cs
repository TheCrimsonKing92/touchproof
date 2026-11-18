using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all process, credential and clock boundaries are injected.
public sealed class ProviderConnectionAdapterTests
{
    [Theory]
    [InlineData("codex-cli", "codex", "login|status", "", "Logged in using ChatGPT")]
    [InlineData("claude-cli", "claude", "auth|status|--json", "{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}", "")]
    public async Task SupportedAdapters_InvokeOnlyStatus(string profile, string cli, string expectedArguments, string stdout, string stderr)
    {
        var calls = 0;
        var adapter = new ProviderConnectionStatusAdapters("fixture", (request, _) =>
        {
            calls++;
            Assert.Equal(cli + ".exe", request.FileName);
            Assert.Equal(expectedArguments.Split('|'), request.Arguments);
            Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
            Assert.Null(request.StandardInput);
            Assert.True(request.RequireOwnedProcessGroup);
            return Task.FromResult(new WorkerProcessRunResult(0, stdout, stderr));
        }, executable => executable + ".exe");
        var observation = await adapter.CheckAsync(WorkerProfileCatalog.Default().GetRequired(profile));
        Assert.Equal(1, calls);
        Assert.Equal(ProviderConnectionResult.SignedIn, observation.Result);
        Assert.Equal(ProviderConnectionContext.Host, observation.ExecutionContext);
        Assert.Equal(ProviderConnectionAuthMode.OAuth, observation.AuthenticationMode);
    }

    [Theory]
    [InlineData("codex", 1, "", "Not logged in", ProviderConnectionResult.SignedOut)]
    [InlineData("codex", 1, "", "Credentials expired", ProviderConnectionResult.Expired)]
    [InlineData("codex", 0, "quoted: Logged in using ChatGPT", "", ProviderConnectionResult.Unknown)]
    [InlineData("codex", 0, "Logged in using ChatGPT", "Token expired", ProviderConnectionResult.Unknown)]
    [InlineData("codex", 1, "", "error: unrecognized subcommand 'status'", ProviderConnectionResult.Unsupported)]
    [InlineData("claude", 1, "{\"error\":\"token_expired\"}", "", ProviderConnectionResult.Expired)]
    [InlineData("claude", 1, "{\"loggedIn\":false}", "", ProviderConnectionResult.SignedOut)]
    [InlineData("claude", 1, "{\"loggedIn\":true}", "", ProviderConnectionResult.Unknown)]
    [InlineData("claude", 0, "{\"message\":\"loggedIn true token expired\"}", "", ProviderConnectionResult.Unknown)]
    [InlineData("claude", 0, "[]", "", ProviderConnectionResult.Unknown)]
    [InlineData("claude", 0, "{\"loggedIn\":true,\"error\":\"token_expired\"}", "", ProviderConnectionResult.Unknown)]
    public void Parsing_IsConservative(string cli, int exit, string stdout, string stderr, ProviderConnectionResult expected)
    {
        Assert.Equal(expected, ProviderConnectionStatusAdapters.Parse(cli, new(exit, stdout, stderr)).Result);
    }

    [Theory]
    [InlineData(0, "", "API key configured - sk-proj-***ABCDE\r\nAuthentication is checked when you send a request.\r\n", true)]
    [InlineData(0, "API key configured - ***\nAuthentication is checked when you send a request.", "", true)]
    [InlineData(1, "", "API key configured - ***\nAuthentication is checked when you send a request.", false)]
    [InlineData(0, "", "API key configured - ***", false)]
    [InlineData(0, "", "API key configured - unexpected\nAuthentication is checked when you send a request.", false)]
    [InlineData(0, "Token expired", "API key configured - ***\nAuthentication is checked when you send a request.", false)]
    public void CodexApiKeyStatus_RequiresSuccessfulRecognizedResponse(int exit, string stdout, string stderr, bool configured)
    {
        var parsed = ProviderConnectionStatusAdapters.Parse("codex", new(exit, stdout, stderr));
        Assert.Equal(configured
            ? (ProviderConnectionResult.MaterialPresent, ProviderConnectionAuthMode.ApiKey)
            : (ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown), parsed);
    }

    [Theory]
    [InlineData("grok-cli", "grok")]
    [InlineData("custom", "codex exec")]
    [InlineData("codex-cli", "arbitrary-launcher")]
    public async Task UnsupportedProfiles_NeverExecuteTemplates(string name, string command)
    {
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => throw new Exception("must not launch"),
            _ => throw new Exception("must not locate"));
        Assert.Equal(ProviderConnectionResult.Unsupported, (await adapter.CheckAsync(new(name, command))).Result);
    }

    [Fact]
    public async Task MissingTimeoutAndOutputLimit_RemainDistinct()
    {
        var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");
        var missing = new ProviderConnectionStatusAdapters("fixture", (_, _) => throw new Exception("must not launch"), _ => null);
        Assert.Equal(ProviderConnectionResult.ToolMissing, (await missing.CheckAsync(profile)).Result);
        foreach (var result in new[] { new WorkerProcessRunResult(-1, "", "", TimedOut: true),
            new WorkerProcessRunResult(-1, "", "", CaptureLimited: true) })
        {
            var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => Task.FromResult(result), _ => "codex.exe");
            Assert.Equal(result.TimedOut ? ProviderConnectionResult.TimedOut : ProviderConnectionResult.OutputLimited,
                (await adapter.CheckAsync(profile)).Result);
        }
    }

    [Fact]
    public async Task ClaudeSelection_IsSharedOnceAndMaterialIsSeparate()
    {
        var calls = 0;
        var store = new MemoryStore();
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(0, "{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}", "")), _ => "claude.exe");
        var service = new ProviderConnectionReadinessService(WorkerProfileCatalog.Default(), store, adapter,
            claudeProbe: () => { calls++; return new(false, false, null, "secret-explicit-path", true); });
        var report = Assert.Single((await service.CheckAsync("CLAUDE-CLI", store)).Profiles);
        Assert.Equal(1, calls);
        Assert.Contains(report.Observations, o => o.Result == ProviderConnectionResult.SignedIn);
        Assert.Contains(report.Observations, o => o.ExecutionContext == ProviderConnectionContext.WorkerCredentialMaterial &&
            o.Result == ProviderConnectionResult.MaterialUnavailable);
        Assert.DoesNotContain(report.Observations, o => o.Result == ProviderConnectionResult.WorkerSucceeded);
    }

    private sealed class MemoryStore : IProviderConnectionEvidenceStore
    {
        private readonly List<ProviderConnectionObservation> _observations = [];
        public Task<IReadOnlyList<ProviderConnectionObservation>> ReadProviderConnectionEvidenceAsync(string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderConnectionObservation>>(_observations.ToArray());
        public Task SaveProviderConnectionObservationAsync(string profileId, ProviderConnectionObservation observation, CancellationToken cancellationToken = default)
        {
            _observations.RemoveAll(o => o.ExecutionContext == observation.ExecutionContext);
            _observations.Add(observation);
            return Task.CompletedTask;
        }
    }
}
