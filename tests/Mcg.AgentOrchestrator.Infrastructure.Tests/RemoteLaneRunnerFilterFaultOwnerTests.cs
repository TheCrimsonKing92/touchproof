using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each coordinator owns its ledger and uses an injected executor and clock.
public sealed class RemoteLaneRunnerFilterFaultOwnerTests
{
    private const string AttemptId = "attempt-one";
    private const string FilterFailure = "FILTER FAILURE - managed build/test processes were not launched";

    [Theory]
    [InlineData(FilterFailure, "runner")]
    [InlineData("COMPLETED WITH MISSING TRX", "executor")]
    public async Task MissingTrxExit26_RecordsFaultOwnerFromCurrentAttemptDiagnosis(string diagnosis, string expectedOwner)
    {
        await using var transport = new FakeSshRemoteLaneTransport();
        var entry = new RemoteLaneExecutorEntry("one", 60);
        var check = new Check
        {
            Name = FakeSshRemoteLaneTransport.Request.Lane,
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", FakeSshRemoteLaneTransport.Request.Filter]
        };
        var executor = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var result = new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                    request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 26, [],
                    TestSettings: request.TestSettings);
                var status = JsonSerializer.SerializeToElement(new { attemptId = request.AttemptId, runnerDiagnosis = new[] { diagnosis } });
                return Task.FromResult(new RemoteLaneSubmission(new StatusHandle(result, status)));
            }
        };
        using var coordinator = new RemoteLaneCoordinator(new([entry], [check.Name], null),
            new RemoteLaneCandidateIdentity(AttemptId, "goal", "commit", "tree", "main", "manifest"),
            transport.Root, Path.Combine(transport.Root, "attempt", "result"), executor, transport.Clock, TimeSpan.FromMilliseconds(1), null);
        Assert.True(coordinator.IsEligible(check));
        Assert.Equal(entry, coordinator.TryClaimIdleExecutor());

        var outcome = await coordinator.RunRemoteAsync(check, entry, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), default);

        Assert.Null(outcome.Accepted);
        Assert.Single(executor.Requests);
        var ledger = RemoteExecutorHealthLedger.ResolveStorePath(transport.Root);
        var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(ledger));
        Assert.Equal(RemoteLaneOutcomeCode.TrxIncomplete, row.Outcome);
        Assert.Equal("exit=26 trx=missing", row.Reason);
        Assert.Equal(expectedOwner, row.FaultOwner);
        using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(ledger)));
        Assert.Equal(expectedOwner, json.RootElement.GetProperty("fault_owner").GetString());
    }

    [Theory]
    [InlineData(null, "executor", "executor")]
    [InlineData("[]", "executor", "executor")]
    [InlineData("""{"runnerDiagnosis":["FILTER FAILURE"]}""", "executor", "executor")]
    [InlineData("""{"attemptId":"old","runnerDiagnosis":["FILTER FAILURE"]}""", "executor", "executor")]
    [InlineData("""{"attemptId":"attempt-one","exitCode":26}""", "executor", "executor")]
    [InlineData("""{"attemptId":"attempt-one","runnerDiagnosis":"FILTER FAILURE"}""", "executor", "executor")]
    [InlineData("""{"attemptId":"attempt-one","runnerDiagnosis":[{},null,"filter failure"]}""", "executor", "executor")]
    [InlineData("""{"attemptId":"attempt-one","runnerDiagnosis":[{},null,"FILTER FAILURE details"]}""", "executor", "runner")]
    [InlineData("""{"attemptId":"attempt-one","runnerDiagnosis":["FILTER FAILURE"]}""", null, null)]
    public void Resolve_RequiresAFailureAndMatchingStatusDiagnosis(string? status, string? owner, string? expectedOwner)
    {
        using var document = status is null ? null : JsonDocument.Parse(status);
        Assert.Equal(expectedOwner, RemoteLaneFaultOwner.Resolve(owner, document?.RootElement, AttemptId));
    }

    private sealed class StatusHandle(RemoteLaneResult result, JsonElement status)
        : IRemoteLaneHandle, IRemoteLaneAttemptDiagnosticsSource
    {
        public DateTimeOffset? NewestHeartbeat => null;
        public RemoteLaneResult TryGetResult() => result;
        public void Abandon() { }
        public RemoteLaneHandleDiagnostics Snapshot() => new(new(1, 0, "completed", null, null), [], status);
        public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteLaneRunnerLogCapture(null));
    }
}
