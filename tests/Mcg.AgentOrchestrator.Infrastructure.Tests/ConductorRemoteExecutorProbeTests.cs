using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each probe owns its directory, transport and manual clock.
public sealed class ConductorRemoteExecutorProbeTests
{
    [Xunit.Fact]
    public async Task EvaluateStartsOneBoundedBackgroundCallAndUsesInjectedTenMinuteCadence()
    {
        using var fixture = new Fixture();
        var called = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<GoalAcceptanceVerifier.CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var probe = fixture.Probe((args, directory, bound, token) =>
        {
            Assert.Equal(fixture.Root, directory);
            Assert.Equal(TimeSpan.FromSeconds(30), bound);
            Interlocked.Increment(ref count);
            called.TrySetResult(args);
            return pending.Task;
        });
        try
        {
            var evaluate = Task.Run(probe.Evaluate);
            var args = await Event(called.Task, "probe transport started");
            await Event(evaluate, "tick returned while transport remains blocked");
            Assert.False(probe.CurrentProbe!.IsCompleted);
            Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                "admin-one", "powershell", "-NoProfile", "-NonInteractive", "-EncodedCommand" }, args[..^1]);
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            Assert.Contains("schtasks /query /tn mcg-executor-lane", script);
            Assert.Contains("BatteryStatus", script);
            probe.Evaluate();
            Assert.Equal(1, count);
            pending.SetResult(Result("Ready", true) with { Stderr = new string('e', 2200) });
            await Event(probe.CurrentProbe, "probe completed");
            var row = Assert.Single(fixture.Rows());
            Assert.Equal("Ready", row.TaskState);
            Assert.True(row.PowerOnline);
            Assert.True(row.Reachable);
            Assert.Equal(0, row.ExitCode);
            Assert.False(row.TimedOut);
            Assert.Equal(2048, row.StderrTail.Length);
            Assert.Equal(fixture.Clock.GetUtcNow(), row.ObservedAt);
            probe.Evaluate();
            Assert.Equal(1, count);
            fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
            probe.Evaluate();
            Assert.Equal(1, count);
            fixture.Clock.Advance(TimeSpan.FromTicks(1));
            probe.Evaluate();
            await Event(probe.CurrentProbe!, "second probe completed");
            Assert.Equal(2, count);
            Assert.Equal(2, fixture.Rows().Count);
            Assert.Empty(fixture.Events());
        }
        finally
        {
            pending.TrySetResult(Result("Ready", true));
            if (probe.CurrentProbe is { } task) await Event(task, "probe cleanup");
        }
    }

    [Xunit.Fact]
    public async Task OccupiedExecutorIsSkippedInConfigurationOrder()
    {
        using var fixture = new Fixture(two: true);
        RemoteExecutorOccupancy.Claim(fixture.Root, "one", "gate");
        string? admin = null;
        var probe = fixture.Probe((args, _, _, _) =>
        { admin = args[5]; return Task.FromResult(Result("Running", true)); });
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "idle executor probe completed");
        Assert.Equal("admin-two", admin);
        var row = Assert.Single(fixture.Rows());
        Assert.Equal("two", row.ExecutorId);
        Assert.Equal("Running", row.TaskState);
        Assert.True(row.PowerOnline);
        Assert.Empty(fixture.Events());
    }

    [Xunit.Theory]
    [Xunit.InlineData(255, false, "{}", "unreachable")]
    [Xunit.InlineData(0, true, "{}", "unreachable")]
    [Xunit.InlineData(0, false, "", "unreachable")]
    [Xunit.InlineData(17, false, "{}", "probe-failed")]
    [Xunit.InlineData(0, false, "{", "probe-failed")]
    public async Task TransportFaultEvidenceDeterminesReason(int exit, bool timedOut, string output, string reason)
    {
        using var fixture = new Fixture();
        var probe = fixture.Probe((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exit, output, timedOut)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "fault probe completed");
        Assert.Equal(new[] { reason }, Assert.Single(fixture.Rows()).Reasons);
        Assert.Contains("REMOTE_EXECUTOR_WARNING executor=one", Assert.Single(fixture.Events()));
        Assert.Contains("reasons=" + reason, Assert.Single(fixture.Events()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "repeat fault probe completed");
        Assert.Single(fixture.Events());
        Assert.Equal(2, fixture.Rows().Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Disabled", true, "task-not-ready")]
    [Xunit.InlineData(null, true, "task-not-ready")]
    [Xunit.InlineData("Ready", false, "on-battery")]
    public async Task ConditionsWarnOnceAndRecoverWithHealthyRunningTask(string? state, bool power, string reason)
    {
        using var fixture = new Fixture();
        var result = Result(state, power);
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "condition probe completed");
        Assert.Equal(new[] { reason }, Assert.Single(fixture.Rows()).Reasons);
        Assert.Contains("reasons=" + reason, Assert.Single(fixture.Events()));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        result = Result("Running", true);
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "recovery probe completed");
        Assert.Equal(2, fixture.Events().Count);
        Assert.Equal("REMOTE_EXECUTOR_RECOVERED executor=one task_state=Running power_online=true disk_free_bytes=null sshd_sessions=null", fixture.Events()[1]);
        Assert.Empty(fixture.Rows()[1].Reasons);
    }

    [Xunit.Fact]
    public async Task ChangedReasonSetWarnsAndRelaunchDoesNotRepeatPersistedWarning()
    {
        using var fixture = new Fixture();
        var result = Result("Ready", false);
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "battery probe completed");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        result = Result("Disabled", false);
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "changed condition completed");
        Assert.Equal(new[] { "task-not-ready", "on-battery" }, fixture.Rows()[1].Reasons);
        Assert.Contains("reasons=task-not-ready,on-battery", fixture.Events()[1]);
        var relaunched = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        relaunched.Evaluate();
        await Event(relaunched.CurrentProbe!, "relaunch probe completed");
        Assert.Equal(2, fixture.Events().Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ThrowingTransportChangesNoLedgerOrConditionState(bool asynchronous)
    {
        using var fixture = new Fixture();
        var fail = false;
        var probe = fixture.Probe((_, _, _, _) =>
        {
            if (!fail) return Task.FromResult(Result("Ready", false));
            if (asynchronous) return Task.FromException<GoalAcceptanceVerifier.CommandResult>(new IOException("fixture"));
            throw new IOException("fixture");
        });
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "initial warning completed");
        var bytes = File.ReadAllBytes(fixture.Ledger);
        fail = true;
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(Xunit.Record.Exception(probe.Evaluate));
        await Event(probe.CurrentProbe!, "throwing probe swallowed");
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Ledger));
        Assert.Single(fixture.Events());
        fail = false;
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "unchanged warning completed");
        Assert.Single(fixture.Events());
    }

    [Xunit.Fact]
    public void AdvisoryEventsClassifyAsDecisionAndOutcome()
    {
        Assert.Equal(ConductEventOperatorClassifier.Decision,
            ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_WARNING executor=one reasons=unreachable"));
        Assert.Equal(ConductEventOperatorClassifier.Outcome,
            ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_RECOVERED executor=one"));
        Assert.Null(ConductEventOperatorClassifier.Classify("remote-executor-health", "REMOTE_EXECUTOR_WARNING_FAKE"));
    }

    [Xunit.Fact]
    public async Task ReadyExecutor_OffersWithoutAcceptance_WarnsWithLatestReasonAndCount()
    {
        using var fixture = new Fixture();
        fixture.Outcome("accepted", 61, "old success");
        fixture.Outcome("lease-expired", 5, "latest lease expired");
        fixture.Outcome("unreachable", 50, "older connection failure");
        // Another executor's acceptance must not hide this executor's stall.
        fixture.Outcome("accepted", 1, "other success", "two");
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(Result("Ready", true)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "ready executor stall probe completed");

        var row = Assert.Single(fixture.Rows());
        Assert.True(row.Reachable);
        Assert.Equal("Ready", row.TaskState);
        Assert.Equal(new[] { "no-accepted-lane" }, row.Reasons);
        var detail = Assert.Single(fixture.Events());
        Assert.StartsWith("REMOTE_EXECUTOR_WARNING executor=one", detail);
        Assert.Contains("reasons=no-accepted-lane", detail);
        Assert.Contains("offers=2", detail);
        Assert.Contains("last_offer_reason=latest lease expired", detail);
        Assert.Equal(ConductEventOperatorClassifier.Decision,
            ConductEventOperatorClassifier.Classify("remote-executor-health", detail));

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Outcome("unreachable", 0, "changed diagnostics");
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "unchanged stall probe completed");
        Assert.Equal(2, fixture.Rows().Count);
        Assert.Single(fixture.Events());
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("old")]
    [Xunit.InlineData("accepted")]
    [Xunit.InlineData("non-attempts")]
    public async Task ReadyExecutor_NoQualifyingStall_EmitsNoWarning(string history)
    {
        using var fixture = new Fixture();
        switch (history)
        {
            case "old": fixture.Outcome("unreachable", 61, "old offer"); break;
            case "accepted":
                fixture.Outcome("unreachable", 10, "failed offer");
                fixture.Outcome("accepted", 5, "success");
                break;
            case "non-attempts":
                foreach (var outcome in new[] { "not-eligible-exclusive-resource", "late-after-fallback",
                    "executor-busy", "preempted-by-gate", "executor-tools-mismatch" })
                    fixture.Outcome(outcome, 5, "not an offer");
                break;
        }
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(Result("Ready", true)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "no stall probe completed");
        Assert.Empty(Assert.Single(fixture.Rows()).Reasons);
        Assert.Empty(fixture.Events());
    }

    [Xunit.Fact]
    public async Task StalledExecutor_AcceptsLane_RecoversExactlyOnce()
    {
        using var fixture = new Fixture();
        fixture.Outcome("executor-tools-unverified", 5, "digest unavailable");
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(Result("Ready", true)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "stall warning completed");
        Assert.Equal(new[] { "no-accepted-lane" }, Assert.Single(fixture.Rows()).Reasons);
        Assert.StartsWith("REMOTE_EXECUTOR_WARNING", Assert.Single(fixture.Events()));

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Outcome("accepted", 1, "lane accepted");
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "stall recovery completed");
        Assert.Empty(fixture.Rows()[1].Reasons);
        var recovered = fixture.Events()[1];
        Assert.StartsWith("REMOTE_EXECUTOR_RECOVERED", recovered);
        Assert.Equal(ConductEventOperatorClassifier.Outcome,
            ConductEventOperatorClassifier.Classify("remote-executor-health", recovered));
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "post recovery probe completed");
        Assert.Empty(fixture.Rows()[2].Reasons);
        Assert.Equal(2, fixture.Events().Count);
        Assert.Single(fixture.Events().Where(detail => detail.StartsWith("REMOTE_EXECUTOR_RECOVERED")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(4123456789L, 54)]
    [Xunit.InlineData(0L, 0)]
    public async Task Telemetry_WarningAndRecovery_PersistsAndReportsMeasurements(long bytes, int sessions)
    {
        using var fixture = new Fixture();
        var result = Result("Disabled", true, bytes, sessions);
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(result));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "telemetry warning completed");
        var row = Assert.Single(fixture.Rows());
        Assert.Equal(bytes, row.DiskFreeBytes);
        Assert.Equal(sessions, row.SshdSessionCount);
        var warning = Assert.Single(fixture.Events());
        Assert.StartsWith("REMOTE_EXECUTOR_WARNING", warning);
        Assert.Contains($"disk_free_bytes={bytes}", warning);
        Assert.Contains($"sshd_sessions={sessions}", warning);
        Assert.DoesNotContain("offers=", warning);

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        result = Result("Running", true, bytes, sessions);
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "telemetry recovery completed");
        Assert.Empty(fixture.Rows()[1].Reasons);
        Assert.Equal(bytes, fixture.Rows()[1].DiskFreeBytes);
        Assert.Equal(sessions, fixture.Rows()[1].SshdSessionCount);
        Assert.Equal(2, fixture.Events().Count);
        var recovery = fixture.Events()[1];
        Assert.StartsWith("REMOTE_EXECUTOR_RECOVERED", recovery);
        Assert.Contains($"disk_free_bytes={bytes}", recovery);
        Assert.Contains($"sshd_sessions={sessions}", recovery);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Ready", "REMOTE_EXECUTOR_RECOVERED")]
    [Xunit.InlineData("Disabled", "REMOTE_EXECUTOR_WARNING")]
    public async Task Stall_AgesOut_UsesRemainingProbeReasons(string state, string nextEvent)
    {
        using var fixture = new Fixture();
        fixture.Outcome("unreachable", 55, "offer nearing expiry");
        var probe = fixture.Probe((_, _, _, _) => Task.FromResult(Result(state, true)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "initial stall probe completed");
        Assert.Contains("no-accepted-lane", Assert.Single(fixture.Rows()).Reasons);
        Assert.Contains("offers=1", Assert.Single(fixture.Events()));

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "expired stall probe completed");
        Assert.Equal(2, fixture.Rows().Count);
        Assert.Equal(state == "Ready" ? Array.Empty<string>() : new[] { "task-not-ready" }, fixture.Rows()[1].Reasons);
        Assert.Equal(2, fixture.Events().Count);
        Assert.StartsWith(nextEvent, fixture.Events()[1]);
        Assert.DoesNotContain("no-accepted-lane", fixture.Events()[1]);
        Assert.DoesNotContain("offers=", fixture.Events()[1]);
    }

    [Xunit.Theory]
    [Xunit.InlineData(255, "unreachable")]
    [Xunit.InlineData(17, "probe-failed")]
    public async Task TransportFault_WithStalledHistory_RecordsBothReasons(int exitCode, string reason)
    {
        using var fixture = new Fixture();
        fixture.Outcome("lease-expired", 5, null);
        var probe = fixture.Probe((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(exitCode, "{}")));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "fault and stall probe completed");
        var row = Assert.Single(fixture.Rows());
        Assert.Equal(new[] { reason, "no-accepted-lane" }, row.Reasons);
        Assert.Null(row.DiskFreeBytes);
        Assert.Null(row.SshdSessionCount);
        var detail = Assert.Single(fixture.Events());
        Assert.Contains("last_offer_reason=null", detail);
        Assert.Contains("disk_free_bytes=null sshd_sessions=null", detail);
    }

    [Xunit.Theory]
    [Xunit.InlineData("null", "54", null, 54)]
    [Xunit.InlineData("4123456789", "null", 4123456789L, null)]
    [Xunit.InlineData("\"unavailable\"", "{}", null, null)]
    [Xunit.InlineData("9223372036854775808", "2147483648", null, null)]
    [Xunit.InlineData("1.5", "1.5", null, null)]
    public async Task Telemetry_UnavailableOrMalformed_PreservesIndependentValues(
        string disk, string sessions, long? expectedDisk, int? expectedSessions)
    {
        using var fixture = new Fixture();
        var output = "{\"task\":\"\\\"mcg-executor-lane\\\",\\\"N/A\\\",\\\"Ready\\\"\",\"powerOnline\":true," +
            $"\"diskFreeBytes\":{disk},\"sshdSessionCount\":{sessions}" + "}";
        var probe = fixture.Probe((_, _, _, _) =>
            Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, output)));
        probe.Evaluate();
        await Event(probe.CurrentProbe!, "optional telemetry probe completed");
        var row = Assert.Single(fixture.Rows());
        Assert.Empty(row.Reasons);
        Assert.Equal(expectedDisk, row.DiskFreeBytes);
        Assert.Equal(expectedSessions, row.SshdSessionCount);
        Assert.Empty(fixture.Events());
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData(",\"disk_free_bytes\":null,\"sshd_session_count\":null")]
    [Xunit.InlineData(",\"disk_free_bytes\":\"bad\",\"sshd_session_count\":1.5")]
    public void ProbeLedger_LegacyOrUnavailableTelemetry_ReadsNulls(string telemetry)
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Ledger)!);
        File.WriteAllText(fixture.Ledger,
            "{\"observed_at\":\"2026-10-06T00:00:00Z\",\"executor_id\":\"one\",\"reachable\":true," +
            "\"exit_code\":0,\"timed_out\":false,\"task_state\":\"Ready\",\"power_online\":true," +
            "\"reasons\":[],\"stderr_tail\":\"\"" + telemetry + "}\n");
        var rows = RemoteExecutorReportReader.ReadProbes(fixture.Ledger, out var unreadable);
        Assert.Equal(0, unreadable);
        var row = Assert.Single(rows);
        Assert.Equal("Ready", row.TaskState);
        Assert.Null(row.DiskFreeBytes);
        Assert.Null(row.SshdSessionCount);
    }

    private static GoalAcceptanceVerifier.CommandResult Result(string? state, bool? power,
        long? diskFreeBytes = null, int? sshdSessionCount = null) => new(0,
        JsonSerializer.Serialize(new { task = state is null ? null : $"\"mcg-executor-lane\",\"N/A\",\"{state}\"",
            powerOnline = power, diskFreeBytes, sshdSessionCount }));
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name) { await Event((Task)task, name); return await task; }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string Configuration => Path.Combine(Root, "executors.json");
        internal string Ledger => Path.Combine(Root, ".orchestrator", RemoteExecutorProbeLedger.FileName);
        private string EventsPath => Path.Combine(Root, "events.log");
        internal Fixture(bool two = false)
        {
            var ids = two ? new[] { "one", "two" } : new[] { "one" };
            File.WriteAllText(Configuration, JsonSerializer.Serialize(new { executors = ids.Select(id => new {
                id, transport = "ssh", runnerAlias = "runner-" + id, adminAlias = "admin-" + id,
                remoteRepository = "C:/repo/bare.git" }), lanes = Array.Empty<string>() }));
        }
        internal ConductorRemoteExecutorProbe Probe(
            Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> transport) =>
            new(Root, Configuration, Clock, transport, RemoteExecutorOccupancy.DefaultStartTime,
                new ConductEventLogWriter(EventsPath));
        internal void Outcome(string outcome, int minutesAgo, string? reason, string executorId = "one")
        {
            var path = Path.Combine(Root, ".orchestrator", RemoteExecutorHealthLedger.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(new RemoteExecutorOutcomeRow(
                Clock.GetUtcNow().AddMinutes(-minutesAgo), executorId, "gate", "lane", outcome, reason),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n");
        }
        internal IReadOnlyList<RemoteExecutorProbeRow> Rows() => RemoteExecutorReportReader.ReadProbes(Ledger, out _);
        internal IReadOnlyList<string> Events() => File.Exists(EventsPath) ? File.ReadAllLines(EventsPath).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            Assert.Equal("remote-executor-health", json.RootElement.GetProperty("eventKind").GetString());
            return json.RootElement.GetProperty("detail").GetString()!;
        }).ToArray() : [];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
