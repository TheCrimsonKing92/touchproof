using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private stores and writers, injected native status responses and credential probe.
public sealed class ProviderConnectionRedactionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderSecrets_NeverEnterReportDiagnosticsOrPersistedStatus(bool json)
    {
        const string secret = "SECRET-token-key-device-code-account-path";
        using var fixture = new GoalTaskHistoryTestFixture();
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => Task.FromResult(new WorkerProcessRunResult(0,
            "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"account\":\"" + secret + "\",\"token\":\"" + secret + "\"}", secret)),
            _ => "claude.exe");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "check", "--profile", "claude-cli", .. (json ? new[] { "--json" } : [])],
            WorkerProfileCatalog.Default(), _ => fixture.Repository, adapter, stdout, stderr,
            claudeProbe: () => new(false, true, secret, secret, true));
        Assert.Equal(0, exit);
        Assert.Contains("SignedIn", stdout.ToString());
        Assert.Contains("MaterialPresent", stdout.ToString());
        Assert.DoesNotContain(secret, stdout.ToString());
        Assert.Empty(stderr.ToString());
        var rows = fixture.Rows("SELECT profile_id || ':' || execution_context || ':' || evidence_source || ':' || result || ':' || authentication_mode || ':' || observed_at FROM provider_connection_observations");
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.DoesNotContain(secret, row));
        Assert.DoesNotContain(secret, File.ReadAllText(fixture.DiagnosticsPath));
    }

    [Fact]
    public async Task CodexMaskedApiKey_IsDiscardedBeforeRenderingAndPersistence()
    {
        const string maskedKey = "KEYPIECE***FRAGM";
        using var fixture = new GoalTaskHistoryTestFixture();
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => Task.FromResult(new WorkerProcessRunResult(0,
            "", $"API key configured - {maskedKey}\r\nAuthentication is checked when you send a request.\r\n")), _ => "codex.exe");
        var catalog = WorkerProfileCatalog.Default();
        foreach (var args in new[] { new[] { "provider-connections", "check", "--profile", "codex-cli", "--json" },
            new[] { "provider-connections" } })
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.Equal(0, await CliProviderConnectionsCommand.ExecuteAsync(
                args, catalog, _ => fixture.Repository, adapter, stdout, stderr));
            var report = stdout.ToString();
            Assert.Contains("MaterialPresent", report);
            Assert.Contains("ApiKey", report);
            Assert.Contains("Service acceptance remains unverified.", report);
            Assert.DoesNotContain("KEYPIECE", report);
            Assert.DoesNotContain("FRAGM", report);
            Assert.Empty(stderr.ToString());
        }

        var saved = Assert.Single(await fixture.Repository.ReadProviderConnectionEvidenceAsync("codex-cli"),
            observation => observation.ExecutionContext == ProviderConnectionContext.Host);
        Assert.Equal(ProviderConnectionSource.CodexLoginStatus, saved.EvidenceSource);
        Assert.Equal(ProviderConnectionResult.MaterialPresent, saved.Result);
        Assert.Equal(ProviderConnectionAuthMode.ApiKey, saved.AuthenticationMode);
        var diagnostics = File.ReadAllText(fixture.DiagnosticsPath);
        Assert.DoesNotContain("KEYPIECE", diagnostics);
        Assert.DoesNotContain("FRAGM", diagnostics);
    }

    [Fact]
    public async Task RawLaunchException_IsNotRenderedOrRecordedAsUnknown()
    {
        const string secret = "SECRET-untrusted-launch-error";
        using var fixture = new GoalTaskHistoryTestFixture();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var adapter = new ProviderConnectionStatusAdapters("fixture", (_, _) => throw new IOException(secret), _ => "codex.exe");
        var exit = await CliProviderConnectionsCommand.ExecuteAsync(["provider-connections", "check", "--profile", "codex-cli", "--json"],
            WorkerProfileCatalog.Default(), _ => fixture.Repository, adapter, stdout, stderr);
        Assert.NotEqual(0, exit);
        Assert.Empty(stdout.ToString());
        Assert.DoesNotContain(secret, stderr.ToString());
        Assert.Contains("could not be produced", stderr.ToString());
        Assert.Equal(["0"], fixture.Rows("SELECT COUNT(*) FROM provider_connection_observations"));
    }

    [Fact]
    public async Task PersistenceRejectsWorkerEvidenceAndInvalidEnums()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.SaveProviderConnectionObservationAsync("codex-cli",
            new(ProviderConnectionContext.Host, ProviderConnectionSource.CodexLoginStatus, (ProviderConnectionResult)999,
                ObservedAt: DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.SaveProviderConnectionObservationAsync("codex-cli",
            new(ProviderConnectionContext.WorkerSandbox, ProviderConnectionSource.RecordedInvocation, ProviderConnectionResult.WorkerSucceeded,
                ObservedAt: DateTimeOffset.UtcNow, IsHistorical: true)));
        Assert.Equal(["0"], fixture.Rows("SELECT COUNT(*) FROM provider_connection_observations"));
    }
}
