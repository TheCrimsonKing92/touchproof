using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: the launcher fixture changes PATH only in its own child process.
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
    [InlineData("codex", 2, "", "error: unrecognized subcommand 'status'\r\n\r\nUsage: codex login [OPTIONS]\r\n", ProviderConnectionResult.Unsupported)]
    [InlineData("claude", 1, "", "error: unknown command 'auth'", ProviderConnectionResult.Unsupported)]
    [InlineData("codex", 1, "", "quoted: error: unknown command 'status'\nUsage: codex login", ProviderConnectionResult.Unknown)]
    [InlineData("codex", 1, "", "error: unknown command 'auth'", ProviderConnectionResult.Unknown)]
    [InlineData("codex", 0, "", "error: unknown command 'status'", ProviderConnectionResult.Unknown)]
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
    [InlineData("Logged in using ChatGPT", "Logged in using an API key")]
    [InlineData("Logged in using API key", "Logged in using ChatGPT")]
    public void CodexSignIn_ConflictingModesRemainUnknown(string stdout, string stderr)
    {
        Assert.Equal((ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.Unknown),
            ProviderConnectionStatusAdapters.Parse("codex", new(0, stdout, stderr)));
    }

    [Theory]
    [InlineData("{\"loggedIn\":false,\"loggedIn\":true,\"authMethod\":\"oauth\"}", ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown)]
    [InlineData("{\"loggedIn\":true,\"loggedIn\":false}", ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown)]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"api_key\",\"authMethod\":\"oauth\"}", ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.Unknown)]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"oauth\",\"authMethod\":\"api_key\"}", ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.Unknown)]
    [InlineData("{\"loggedIn\":true,\"loggedIn\":true,\"authMethod\":\"oauth\",\"authMethod\":\"claude.ai\"}", ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.OAuth)]
    [InlineData("{\"error\":\"access_denied\",\"error\":\"token_expired\"}", ProviderConnectionResult.Unknown, ProviderConnectionAuthMode.Unknown)]
    public void ClaudeStatus_DuplicateFieldsMustAgree(string output, ProviderConnectionResult result, ProviderConnectionAuthMode mode)
    {
        Assert.Equal((result, mode), ProviderConnectionStatusAdapters.Parse("claude", new(0, output, "")));
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
    [InlineData("codex-cli", "& 'C:\\configured tools\\codex.exe' exec")]
    [InlineData("claude-cli", "./configured/claude -p")]
    [InlineData("codex-cli", "codex.ps1 exec")]
    [InlineData("codex-cli", "$env:PATH='C:\\alternate'; codex exec")]
    [InlineData("claude-cli", "$env:CLAUDE_CONFIG_DIR='C:\\alternate'; claude -p")]
    [InlineData("codex-cli", "Set-Location 'C:\\alternate'; codex exec")]
    [InlineData("codex-cli", "codex exec -c cli_auth_credentials_store=file")]
    public async Task UnsupportedProfiles_NeverExecuteTemplates(string name, string command)
    {
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => throw new Exception("must not launch"),
            _ => throw new Exception("must not locate"));
        Assert.Equal(ProviderConnectionResult.Unsupported, (await adapter.CheckAsync(new(name, command))).Result);
    }

    [Theory]
    [InlineData(".bat", ProviderConnectionResult.Unsupported)]
    [InlineData(".cmd", ProviderConnectionResult.SignedIn)]
    [InlineData(".ps1", ProviderConnectionResult.SignedIn)]
    public async Task WindowsHostProbe_UsesFirstLauncherResolvedByDispatchShell(string extension, ProviderConnectionResult expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("mcg-status-launcher-");
        try
        {
            var earlier = root.CreateSubdirectory("earlier").FullName;
            var later = root.CreateSubdirectory("later").FullName;
            var launcher = Path.Combine(earlier, "codex" + extension);
            const string script = "@echo %*>\"%~dp0invocation.txt\"\r\n@echo Logged in using ChatGPT\r\n@exit /b 0\r\n";
            File.WriteAllText(launcher, extension == ".ps1"
                ? "$args -join ' ' | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'invocation.txt')\n'Logged in using ChatGPT'\nexit 0"
                : script);
            File.WriteAllText(Path.Combine(later, "codex.cmd"), script);

            var childPath = (earlier + ";" + later).Replace("'", "''");
            var expectedPath = launcher.Replace("'", "''");
            var setup = $$"""
                $env:PATH = '{{childPath}}'
                $env:PATHEXT = '.EXE;.BAT;.CMD'
                if ((Get-Command codex).Path -ne '{{expectedPath}}') { throw 'Fixture did not select the earlier launcher' }
                """;
            var adapter = new ProviderConnectionStatusAdapters(root.FullName, (request, token) =>
            {
                Assert.Equal(WorkerShell.Executable, request.FileName);
                Assert.Contains("$PSModuleAutoLoadingPreference = 'None'", request.Arguments[^1]);
                return WorkerProcessRunner.RunBoundedDirectAsync(request with
                {
                    Arguments = [.. request.Arguments.SkipLast(1), setup + "\n" + request.Arguments[^1]],
                    Timeout = TestHangGuard.Bound
                }, ProviderConnectionStatusAdapters.MaximumCaptureBytes, token);
            });

            var observation = await adapter.CheckAsync(WorkerProfileCatalog.Default().GetRequired("codex-cli"));
            Assert.Equal(expected, observation.Result);
            var earlierInvocation = Path.Combine(earlier, "invocation.txt");
            if (expected == ProviderConnectionResult.SignedIn)
                Assert.Equal("login status", File.ReadAllText(earlierInvocation).Trim());
            else
                Assert.False(File.Exists(earlierInvocation));
            Assert.False(File.Exists(Path.Combine(later, "invocation.txt")));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root.FullName);
        }
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
