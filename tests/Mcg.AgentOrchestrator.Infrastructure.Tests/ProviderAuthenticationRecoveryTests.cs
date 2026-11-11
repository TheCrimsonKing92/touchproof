using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private database/workspace, injected time and process responses; no credentials.
public sealed class ProviderAuthenticationRecoveryTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(160)]
    public async Task AuthenticationThenUnknownThenAvailable_PersistsTruthfulLifecycle(int width)
    {
        using var fixture = new ProviderHoldStandaloneProbeTests.Fixture("failed");
        fixture.Kernel.CancelGoal(fixture.Source.Id, "Retain the exhausted source for recovery.");
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        kernel.ProviderHoldHistory.Retain(fixture.Source, kernel.Goals);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);
        await repository.SaveAsync(fixture.Kernel, TestContext.Current.CancellationToken);
        var recorder = new ProviderResetRetainedSourceRecorder(() => repository);
        var responses = new Queue<WorkerProcessRunResult>([
            new(1, "", "No auth credentials for cli-chat-proxy\nNot signed in", false),
            new(1, "", "connection refused", false),
            new(0, "AVAILABLE", "", false)]);
        var calls = 0;
        var adapter = new ProviderHoldAvailabilityProbe(WorkerProfileCatalog.Default(), (_, _) =>
        {
            calls++;
            return Task.FromResult(responses.Dequeue());
        });
        var prober = new ProviderHoldStandaloneProbe(adapter, fixture.Writer, fixture.Preflight,
            recorder.RecordCheck, () => fixture.Clock.UtcNow);
        fixture.Clock.UtcNow = fixture.Due;
        prober.ObserveTick(kernel, fixture.Clock.UtcNow);
        var authAt = fixture.Clock.UtcNow;
        var row = Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal("authentication-required", row.Schedule!.LastProbeOutcome);
        Assert.Equal(ProviderFailureKind.AuthenticationRequired, row.Schedule.LastActionableFailure);
        Assert.Equal(authAt, row.Schedule.LastActionableFailureAt);
        Assert.Equal(fixture.Source.Tasks.Single().LastVerification!.CompletedAt, row.Hold.PlacedAt);
        var stored = await repository.LoadAsync(TestContext.Current.CancellationToken);
        var persisted = Assert.Single(ProviderUsageHoldView.Read(stored.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal(row.Schedule, persisted.Schedule);
        var check = Assert.Single(stored.ProviderHoldScope.SelectMany(goal => goal.Timeline)
            .Where(evt => ProviderBudgetHoldSchedules.TryParseStandaloneResult(evt, out _)));
        Assert.True(ProviderBudgetHoldSchedules.TryParseStandaloneResult(check, out var result));
        Assert.Equal(ProviderFailureKind.AuthenticationRequired, result.FailureKind);
        Assert.True(OwnerProviderHoldNarration.TryNarrate(fixture.Events().Last(), out var phrase, out _, out _, out var action));
        Assert.Contains("sign-in required", phrase);
        Assert.Contains("Sign in", action);

        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        prober.ObserveTick(kernel, fixture.Clock.UtcNow);
        row = Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Equal("inconclusive", row.Schedule!.LastProbeOutcome);
        Assert.Equal(authAt, row.Schedule.LastActionableFailureAt);
        Assert.Equal(fixture.Clock.UtcNow, row.Schedule.LastProbeAt);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ProviderUsageHoldJson.Row(row)));
        Assert.Equal("inconclusive", json.RootElement.GetProperty("lastProbe").GetString());
        Assert.Equal(fixture.Clock.UtcNow, json.RootElement.GetProperty("lastProbeAt").GetDateTimeOffset());
        Assert.Equal(nameof(ProviderFailureKind.AuthenticationRequired), json.RootElement.GetProperty("lastActionableFailure").GetString());
        Assert.Equal(authAt, json.RootElement.GetProperty("lastActionableFailureAt").GetDateTimeOffset());
        Assert.Equal(row.Diagnosis, json.RootElement.GetProperty("diagnosis").GetString());
        Assert.Equal(row.Hold.PlacedAt, json.RootElement.GetProperty("placedAt").GetDateTimeOffset());
        var snapshot = ConductorStatusSnapshotReader.Read(fixture.Workspace, null, fixture.Clock.UtcNow,
            now: fixture.Clock.UtcNow, fleetSummaryExpected: false) with { ProviderHolds = [row] };
        using var statusOutput = new OwnerConsoleStructuredWriter();
        CliConductorStatusReader.Print(snapshot, statusOutput);
        var status = statusOutput.ToString();
        Assert.Contains($"    diagnosis: {row.Diagnosis}", status.Split(Environment.NewLine));
        var diagnosisFields = Assert.Single(statusOutput.Build(null)!.Rows!.Values
            .Where(fields => fields.Fields.Any(field => field.Key == "diagnosis")));
        Assert.Equal(row.Hold.BindingKey, diagnosisFields.Identity);
        Assert.Equal(row.Diagnosis, Assert.Single(diagnosisFields.Fields.Where(field => field.Key == "diagnosis")).Value);
        Assert.Contains("last confirmed: authentication-required", status);
        Assert.Contains("age 3600s", status);
        Assert.Contains("historical exhaustion:", status);
        Assert.Contains("recovery: sign in", status);
        Assert.True(OwnerProviderHoldNarration.TryNarrate(fixture.Events().Last(), out phrase, out var reason, out _, out _));
        Assert.Contains("sign-in required", phrase);
        Assert.Contains("latest check: inconclusive", reason);
        var console = OwnerFleetDiagram.Render(OwnerFleetViewModelBuilder.Build(
            RemoteExecutorReport.Build([], [], []), true, TimeZoneInfo.Utc, providerHolds: [row]), width);
        var readable = string.Join(" ", console.Split('\n').Where(line => line.StartsWith('║'))
            .Select(line => line.Trim('║', ' ')));
        Assert.Contains("latest check: inconclusive", readable);
        Assert.Contains("last confirmed: authentication-required", readable);
        Assert.Contains(ProviderBudgetHoldSchedules.FormatInstant(authAt), readable);
        Assert.Contains("age 3600s", readable);
        Assert.Contains("recovery: sign in", readable);
        Assert.Contains("historical exhaustion:", readable);
        Assert.Contains(row.Hold.EvidenceReceipt, readable.Replace(" ", "", StringComparison.Ordinal));
        Assert.DoesNotContain("has no usage balance left", console);

        fixture.Clock.UtcNow = row.Schedule.NextProbeAt;
        prober.ObserveTick(kernel, fixture.Clock.UtcNow);
        Assert.Empty(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, fixture.Clock.UtcNow));
        Assert.Empty(ProviderUsageHoldReader.ReadWorkspace(fixture.Workspace, fixture.Clock.UtcNow));
        var recovered = await repository.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(GoalStatus.Cancelled, recovered.GetGoal(fixture.Source.Id).Status);
        Assert.Equal(3, calls);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData("No auth credentials for cli-chat-proxy", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("Not signed in", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("Error: invalid API key", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("Error: credentials have expired", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("ERROR: Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again.", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("{\"error\":{\"message\":\"401 Unauthorized\"}}", ProviderFailureKind.AuthenticationRequired)]
    [InlineData("403 Forbidden", ProviderFailureKind.Unknown)]
    [InlineData("Permission denied: C:\\credentials", ProviderFailureKind.Unknown)]
    [InlineData("The test expects Not signed in", ProviderFailureKind.Unknown)]
    [InlineData("The test expects ERROR: Your access token could not be refreshed because your refresh token was already used.", ProviderFailureKind.Unknown)]
    [InlineData("```text\nERROR: Your access token could not be refreshed because your refresh token was already used.\n```", ProviderFailureKind.Unknown)]
    [InlineData("WORKER_RESULT:\nNot signed in\nEND_WORKER_RESULT", ProviderFailureKind.Unknown)]
    [InlineData("API error (status 402 Payment Required): Grok Build usage balance exhausted", ProviderFailureKind.BudgetExhausted)]
    public void Adapter_UsesAuthoritativeFailureEvidence(string stderr, ProviderFailureKind expected)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile("grok-cli");
        Assert.Equal(expected, provider.ParseOutcome(new(1, "", stderr)));
        Assert.Equal(ProviderFailureKind.Unknown, provider.ParseOutcome(new(0, "AVAILABLE", stderr)));
    }
}
