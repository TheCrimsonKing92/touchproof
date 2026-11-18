using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all I/O is injected and each invocation has private writers and store.
public sealed class CliProviderConnectionsContractTests
{
    [Theory]
    [InlineData("check")]
    [InlineData("check|--profile")]
    [InlineData("check|--profile|missing")]
    [InlineData("--profile|codex-cli")]
    [InlineData("--json|--json")]
    [InlineData("check|--profile|codex-cli|--bad")]
    [InlineData("check|--profile|codex-cli|--profile|codex-cli")]
    public async Task InvalidInvocationOrSelection_ProducesNonzeroWithoutOpeningOrProbing(string suffix)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", .. suffix.Split('|')],
            WorkerProfileCatalog.Default(), _ => throw new Exception("must not open"), NoProbe(), output, error);
        Assert.NotEqual(0, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task AmbiguousSelection_IsRejectedBeforeOpeningStore()
    {
        var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "check", "--profile", "CODEX-CLI"],
            new([profile, profile with { Name = "CODEX-CLI" }]), _ => throw new Exception("must not open"), NoProbe(), output, error));
        Assert.Contains("unambiguous", error.ToString());
    }

    [Fact]
    public async Task Listing_IsReadOnlyAndTextJsonShareEvidenceIncludingConflict()
    {
        var at = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new Store
        {
            Observations = [new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus,
                ProviderConnectionResult.SignedIn, ProviderConnectionAuthMode.OAuth, at),
                new(ProviderConnectionContext.WorkerSandbox, ProviderConnectionSource.RecordedInvocation,
                    ProviderConnectionResult.AuthenticationFailed, ObservedAt: at.AddHours(1), IsHistorical: true)]
        };
        var catalog = new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired("codex-cli")]);
        using var text = new StringWriter();
        using var json = new StringWriter();
        using var error = new StringWriter();
        var now = new FixedTime(at.AddDays(1));
        IProviderConnectionEvidenceQueries Open(bool write) { Assert.False(write); return store; }
        Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections"], catalog, Open, NoProbe(), text, error, now));
        Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "--json"], catalog, Open, NoProbe(), json, error, now));
        using var document = JsonDocument.Parse(json.ToString());
        var envelope = document.RootElement;
        Assert.Equal("mcg.provider-connections", envelope.GetProperty("schema").GetString());
        Assert.Equal(1, envelope.GetProperty("schemaVersion").GetInt32());
        var profile = Assert.Single(envelope.GetProperty("payload").GetProperty("profiles").EnumerateArray());
        Assert.Equal("codex-cli", profile.GetProperty("profileId").GetString());
        Assert.Equal("OpenAI", profile.GetProperty("provider").GetString());
        Assert.Equal(2, profile.GetProperty("observations").GetArrayLength());
        foreach (var observation in profile.GetProperty("observations").EnumerateArray())
        {
            foreach (var field in new[] { "executionContext", "authenticationMode", "evidenceSource", "result", "reason", "nextAction" })
                Assert.Contains(observation.GetProperty(field).GetString()!, text.ToString());
            Assert.Contains(observation.GetProperty("observedAt").GetDateTimeOffset().ToString("O"), text.ToString());
        }
        Assert.Equal(86400, profile.GetProperty("observations")[0].GetProperty("ageSeconds").GetDouble());
        Assert.True(profile.GetProperty("observations")[1].GetProperty("isHistorical").GetBoolean());
        Assert.Equal(0, store.Writes);
        Assert.Empty(error.ToString());
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["provider-connections"]));
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["provider-connections"]));
        Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(["provider-connections", "check", "--profile", "codex-cli"]));
    }

    [Theory]
    [InlineData(ProviderConnectionResult.ToolMissing)]
    [InlineData(ProviderConnectionResult.TimedOut)]
    [InlineData(ProviderConnectionResult.Unsupported)]
    [InlineData(ProviderConnectionResult.Expired)]
    [InlineData(ProviderConnectionResult.Unknown)]
    public async Task CompletedReports_ReturnZeroForEveryReadinessState(ProviderConnectionResult status)
    {
        var store = new Store { Observations = [new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, status)] };
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "--json"],
            new([WorkerProfileCatalog.Default().GetRequired("codex-cli")]), _ => store, NoProbe(), output, error));
        Assert.Contains(status.ToString(), output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Check_UsesCanonicalIdentityAndPersistsExactlyOneHostObservation()
    {
        var store = new Store();
        var calls = 0;
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(1, "", "Not logged in"));
        }, _ => "codex.exe");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "check", "--profile", "CODEX-CLI", "--json"],
            WorkerProfileCatalog.Default(), write => { Assert.True(write); return store; }, adapter, output, error));
        Assert.Equal(1, calls);
        Assert.Equal(1, store.Writes);
        Assert.Equal("codex-cli", store.LastProfile);
        Assert.Contains("SignedOut", output.ToString());
    }

    [Fact]
    public async Task EmptyCatalog_ReturnsSuccessfulEmptyReport()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "--json"], new([]), _ => new Store(), NoProbe(), output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, json.RootElement.GetProperty("payload").GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public async Task PublicStartup_ListsWithoutDatabaseWritesAndSanitizesInvalidArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "provider-connections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveProviderConnectionObservationAsync("codex-cli",
                new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, ProviderConnectionResult.SignedOut,
                    ObservedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            var before = SHA256.HashData(File.ReadAllBytes(workspace.SqliteStatePath));
            var listing = await RunPublicAsync(root, ["provider-connections", "--json"]);
            Assert.True(listing.ExitCode == 0, listing.StandardError);
            using var document = JsonDocument.Parse(listing.StandardOutput);
            Assert.Equal("mcg.provider-connections", document.RootElement.GetProperty("schema").GetString());
            var profiles = document.RootElement.GetProperty("payload").GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Equal(WorkerProfileCatalog.Default().Profiles.Count, profiles.Length);
            Assert.Contains("SignedOut", listing.StandardOutput);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(workspace.SqliteStatePath)));
            var invalid = await RunPublicAsync(root, ["provider-connections", "--SECRET-flag"]);
            Assert.NotEqual(0, invalid.ExitCode);
            Assert.Empty(invalid.StandardOutput);
            Assert.Contains("Usage:", invalid.StandardError);
            Assert.DoesNotContain("SECRET", invalid.StandardError);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(workspace.SqliteStatePath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<CliChildProcessResult> RunPublicAsync(string root, string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root
        };
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "DOTNET_ROOT",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = root;
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(root, ".orchestrator", "test-dotnet");
        return CliChildProcessRunner.RunAsync(start);
    }

    private static ProviderConnectionStatusAdapters NoProbe() => new("fixture",
        (_, _) => throw new Exception("must not probe"), _ => throw new Exception("must not inspect tools"));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Store : IProviderConnectionEvidenceStore
    {
        internal IReadOnlyList<ProviderConnectionObservation> Observations = [];
        internal int Writes;
        internal string? LastProfile;
        public Task<IReadOnlyList<ProviderConnectionObservation>> ReadProviderConnectionEvidenceAsync(string profileId, CancellationToken cancellationToken = default) => Task.FromResult(Observations);
        public Task SaveProviderConnectionObservationAsync(string profileId, ProviderConnectionObservation observation, CancellationToken cancellationToken = default)
        {
            Writes++;
            LastProfile = profileId;
            Observations = [observation];
            return Task.CompletedTask;
        }
    }
}
