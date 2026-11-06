using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns stable slots and verifier hooks in the existing JobAccounting collection.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsLanePlacementReceipt : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public async Task DeclinedOffer_RecordsLocalPlacementAndExactCoordinatorReason()
    {
        using var scenario = CreateScenario("declined");
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(scenario.Fake.Requests);
        var row = Assert.Single(scenario.LaneRows, row => row.GetProperty("lane").GetString() == Lane(2));
        Assert.Equal("local", row.GetProperty("placement").GetString());
        Assert.Equal("offer-skipped-no-executor", row.GetProperty("offer_outcome").GetString());
        var line = Assert.Single(scenario.Events, line => line.Contains("outcome=offer-skipped-no-executor", StringComparison.Ordinal));
        Assert.Equal(line[(line.IndexOf(" reason=", StringComparison.Ordinal) + 8)..], row.GetProperty("offer_reason").GetString());
        AssertNeverOffered(scenario.LaneRows.Where(row => row.GetProperty("lane").GetString() != Lane(2)));
        var summary = Assert.Single(scenario.Summaries);
        Assert.Equal(6, summary.EnumerateObject().Count());
        Assert.Equal(1, summary.GetProperty("version").GetInt32());
        Assert.Equal(scenario.Goal.Value, summary.GetProperty("goal_id").GetString());
        Assert.Equal(1, summary.GetProperty("lanes_offered").GetInt32());
        Assert.Equal(0, summary.GetProperty("lanes_remote").GetInt32());
    }

    [Fact]
    public async Task AcceptedRemote_RecordsExecutorDurationAndUnavailableAccountingWithoutSummary()
    {
        using var scenario = CreateScenario("accepted");
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(2), Assert.Single(scenario.Fake.Requests).Lane);
        Assert.Equal(new[] { 0, 1 }, scenario.LocalStarts.Order().ToArray());
        var row = Assert.Single(scenario.LaneRows, row => row.GetProperty("lane").GetString() == Lane(2));
        Assert.Equal("fixture-executor", row.GetProperty("placement").GetString());
        Assert.Equal(12500d, row.GetProperty("wall_ms").GetDouble());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("wall_ms_unavailable_reason").ValueKind);
        Assert.Equal("accepted", row.GetProperty("offer_outcome").GetString());
        Assert.Equal("none", row.GetProperty("offer_reason").GetString());
        foreach (var field in new[] { "cpu_ms", "peak_mem_bytes", "host_available_memory_mb" })
        {
            Assert.Equal(JsonValueKind.Null, row.GetProperty(field).ValueKind);
            Assert.Equal("remote-not-reported", row.GetProperty(field + "_unavailable_reason").GetString());
        }
        Assert.Equal("remote-not-reported", row.GetProperty("slot_timing_unavailable_reason").GetString());
        Assert.False(row.TryGetProperty("slot_wait_ms", out _));
        Assert.Equal("not-sampled", row.GetProperty("sched_unavailable_reason").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("sched_samples").ValueKind);
        Assert.Empty(GateLaneBaselineStore.ReadHistory(scenario.Root, Lane(2)));
        Assert.Empty(scenario.Summaries);
    }

    [Fact]
    public async Task NoOffers_RecordsNullOfferFieldsAndNoSummary()
    {
        using var scenario = CreateScenario("none");
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(scenario.Fake.Requests);
        Assert.Equal(3, scenario.LaneRows.Length);
        AssertNeverOffered(scenario.LaneRows);
        Assert.Empty(scenario.Summaries);
    }

    [Theory]
    [InlineData("declined", 0)]
    [InlineData("accepted", 0)]
    [InlineData("declined", 7)]
    public async Task ReceiptAppendThrows_PreservesLaneResultsAndGateVerdict(string mode, int exitCode)
    {
        using var control = CreateScenario(mode);
        var baseline = await control.RunAsync(exitCode);
        Assert.NotEmpty(control.LaneRows);
        using var faulted = CreateScenario(mode);
        var receiptPath = GateLaneReceiptLedger.ResolveStorePath(faulted.Root);
        Directory.CreateDirectory(receiptPath); // AppendLine must fail on this directory.
        var result = await faulted.RunAsync(exitCode);
        Assert.True(Directory.Exists(receiptPath));
        Assert.False(File.Exists(receiptPath));
        Assert.Equal(exitCode == 0, baseline.Passed);
        Assert.Equal(baseline.Passed, result.Passed);
        Assert.Equal(baseline.ExitCode, result.ExitCode);
        Assert.Equal(baseline.Checks!.Select(check => (check.Name, check.Passed, check.ExitCode)),
            result.Checks!.Select(check => (check.Name, check.Passed, check.ExitCode)));
        Assert.NotEmpty(result.Checks.Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Collector_LatestPairAndDistinctCounts_AttemptsSummaryOnce()
    {
        var root = Directory.CreateTempSubdirectory("offer-collector-").FullName;
        try
        {
            var collector = new GateLaneOfferOutcomeCollector();
            collector.Record("lane", "first", "first reason");
            collector.Record("lane", "last", "last reason");
            Assert.Equal(new GateLaneOfferOutcome("last", "last reason"), collector.Latest("lane"));
            Assert.Null(collector.Latest("never offered"));
            Assert.Equal(1, collector.OfferedCount);
            Assert.Equal(0, collector.RemoteCount);
            var guard = collector.BeginBatchSummary(root, "goal", TimeProvider.System);
            guard.Dispose();
            guard.Dispose();
            using (collector.BeginBatchSummary(root, "goal", TimeProvider.System)) { }
            var row = Assert.Single(ReadRows(root));
            Assert.Equal("gate_offer_summary", row.GetProperty("row_kind").GetString());
            Assert.Equal(1, row.GetProperty("lanes_offered").GetInt32());
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public async Task Summary_WaitsForEarlyOfferSettlementBeforeCounting()
    {
        var root = Directory.CreateTempSubdirectory("offer-summary-settlement-").FullName;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var collector = new GateLaneOfferOutcomeCollector();
            var guard = collector.BeginBatchSummary(root, "goal", TimeProvider.System, async () =>
            {
                entered.SetResult();
                await release.Task;
                collector.Record("lane", "cancelled", "settled");
            });
            var disposing = guard.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only bound for the settlement callback.
            Assert.False(File.Exists(GateLaneReceiptLedger.ResolveStorePath(root)));
            release.SetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only bound for summary finalization.
            var row = Assert.Single(ReadRows(root));
            Assert.Equal(1, row.GetProperty("lanes_offered").GetInt32());
            Assert.Equal(0, row.GetProperty("lanes_remote").GetInt32());
        }
        finally { release.TrySetResult(); DeleteDirectoryWithRetry(root); }
    }

    [Fact]
    public void ThrowingAppender_IsInvokedForEachReceiptWithoutEscaping()
    {
        var root = Directory.CreateTempSubdirectory("offer-throwing-appender-").FullName;
        try
        {
            var result = new AcceptanceCheckResult("lane", true, 0, null);
            var calls = 0;
            void Throw(string path, string row) { calls++; throw new IOException("fixture append failure"); }
            var now = DateTimeOffset.UnixEpoch;
            var offer = new GateLaneOfferOutcome("accepted", "none");
            var control = GateLaneReceiptLedger.Record(root, "goal", "lane", result, TimeSpan.FromSeconds(1), now, (_, _) => { });
            var faulted = GateLaneReceiptLedger.Record(root, "goal", "lane", result, TimeSpan.FromSeconds(1), now, Throw, offer: offer);
            Assert.Equal(control!.Status, faulted!.Status);
            GateLaneReceiptLedger.RecordRemote(root, "goal", "lane", result, "executor", TimeSpan.FromSeconds(1), now, offer, Throw);
            GateLaneReceiptLedger.RecordOfferSummary(root, "goal", 1, 0, now, Throw);
            Assert.Equal(3, calls);
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private static void AssertNeverOffered(IEnumerable<JsonElement> rows)
    {
        var values = rows.ToArray();
        Assert.NotEmpty(values);
        Assert.All(values, row =>
        {
            Assert.Equal("local", row.GetProperty("placement").GetString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("offer_outcome").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("offer_reason").ValueKind);
        });
    }

    private Scenario CreateScenario(string mode)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var lanes = Enumerable.Range(0, 3).Select(index => new
        { name = $"Lane {index}", filter = Filter(index), estimatedSerialSeconds = 999 });
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": {{JsonSerializer.Serialize(lanes)}},
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp", "timeoutMinutes": 5,
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }], "forbiddenChangedPathGlobs": []
            }
            """);
        var source = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(source);
        foreach (var index in Enumerable.Range(0, 3))
            File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"), $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        var scenario = new Scenario(this, root, mode);
        var localPath = AcceptanceLaneDurationStore.ResolveStorePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var local = new[] { 45d, 300d, mode == "none" ? 400d : 150d };
        File.WriteAllLines(localPath, Enumerable.Range(0, 3).SelectMany(index => Enumerable.Range(0, 3).Select(_ =>
            JsonSerializer.Serialize(new { version = 1, laneName = Lane(index), filterHash = GoalAcceptanceVerifier.ShortHash(Filter(index)), durationSeconds = local[index], recordedAt = scenario.Clock.GetUtcNow() }))));
        var config = Path.Combine(root, "executors.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            executors = new[] { new { id = "fixture-executor", leaseSeconds = 60, slots = 3, costBudget = 6.0 } },
            lanes = Enumerable.Range(0, 3).Select(Lane).ToArray()
        }));
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = config;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLaneExecutorForTests = scenario.Fake;
        TestOverrides.OnRemoteLaneEventForTests = scenario.Events.Enqueue;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";
    private static JsonElement[] ReadRows(string root)
    {
        var path = GateLaneReceiptLedger.ResolveStorePath(root);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }

    private sealed class Scenario : IDisposable
    {
        private readonly GoalAcceptanceVerifierDotnetBuildSlotTestsLanePlacementReceipt _owner;
        private readonly string _mode;
        internal Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsLanePlacementReceipt owner, string root, string mode)
        {
            _owner = owner; Root = root; _mode = mode;
            Fake.Submit = (request, _) =>
            {
                var directory = Path.Combine(Root, "remote-receipts");
                WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", "remote.trx"], 1, ["Lane2Tests.Executes"]);
                var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
                handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                    request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0,
                    [Path.Combine(directory, "remote.trx")], TestSettings: request.TestSettings, TestSettingsHost: request.TestSettingsHost));
                Clock.Advance(TimeSpan.FromMilliseconds(12500));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            };
        }
        internal string Root { get; }
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Fake { get; } = new();
        internal ConcurrentQueue<string> Events { get; } = new();
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal JsonElement[] LaneRows => ReadRows(Root).Where(row => row.TryGetProperty("lane", out _)).ToArray();
        internal JsonElement[] Summaries => ReadRows(Root).Where(row => row.TryGetProperty("row_kind", out _)).ToArray();

        internal async Task<AcceptanceVerificationResult> RunAsync(int exitCode = 0)
        {
            var claims = new List<IDisposable>();
            try
            {
                if (_mode == "declined")
                    for (var slotIndex = 0; slotIndex < 3; slotIndex++)
                    {
                        var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, "fixture-executor", slotIndex);
                        Assert.NotNull(claim);
                        claims.Add(claim);
                    }
                using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
                var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
                var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
                var attempt = Guid.NewGuid().ToString("N");
                await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                    new AcceptanceAttemptIdentity(attempt, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                        Path.Combine(Root, ".orchestrator", "attempts", attempt, "result"), slot, Environment.ProcessId, null),
                    AcceptanceGateEngineSettings.Load(Root), CancellationToken.None);
                return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
            }
            finally { foreach (var claim in claims) claim.Dispose(); }

            Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
            {
                if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
                {
                    var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                    LocalStarts.Enqueue(index);
                    WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exitCode, "deterministic lane verdict"));
                }
                if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
                {
                    const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                    var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
                    File.WriteAllText(Path.Combine(output, project + (OperatingSystem.IsWindows() ? ".exe" : "")), "fixture");
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            }
        }

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
