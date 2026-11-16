using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: private filesystem roots, fixed observation time, and injected process observations.
public sealed class GateStatusRemoteWaitDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void Read_LiveOwner_IdentifiesAttemptWithoutWriting()
    {
        using var fixture = new Fixture();
        var before = Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)));
        var row = Assert.Single(fixture.Read());
        Assert.True(row.IdentityVerified);
        Assert.Equal("abcdef12abcdef12", row.GoalId);
        Assert.Equal("attempt", row.AttemptId);
        Assert.Equal(123, row.OwnerProcessId);
        Assert.Equal("candidate-sha", row.CandidateSha);
        Assert.Equal(Now.AddSeconds(-30), row.LastProgressAt);
        Assert.Equal(TimeSpan.FromSeconds(30), row.Age);
        Assert.Equal(GateStatusRemoteWaitDetail.OwnerCheck.Alive, row.Owner);
        Assert.Equal("running", row.State);
        Assert.False(row.Stale);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(fixture.Root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in before)
            Assert.Equal(pair.Value, (File.ReadAllText(pair.Key), File.GetLastWriteTimeUtc(pair.Key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_PassedMetadata_IsTerminalDespiteRunningHeartbeat(bool namedOutcome)
    {
        using var fixture = new Fixture();
        fixture.Metadata(outcome: namedOutcome ? "Passed" : (object)1);
        var row = Assert.Single(fixture.Read());
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, row.Outcome);
        Assert.Equal("terminal", row.State);
        var output = CaptureConsole(() => GateStatusTextView.Print([], Now, 1, [row]));
        Assert.Contains("outcome=Passed owner=alive state=terminal", output);
        Assert.DoesNotContain("state=running", output);
    }

    [Theory]
    [InlineData("absent", "dead")]
    [InlineData("recycled", "dead")]
    [InlineData("inaccessible", "unverified")]
    [InlineData("missing-start", "unverified")]
    [InlineData("probe-error", "unverified")]
    public void Read_OwnerEvidence_DoesNotClaimRunningHealth(string kind, string expected)
    {
        using var fixture = new Fixture();
        if (kind == "missing-start") fixture.Metadata(withStart: false);
        var row = Assert.Single(GateStatusRemoteWaitDetail.Read(fixture.Root, Now, Window,
            _ => kind == "probe-error" ? throw new UnauthorizedAccessException() :
                kind == "recycled" ? Now.AddHours(-2) : null,
            _ => kind == "absent"));
        Assert.Equal(expected, row.Owner.ToString().ToLowerInvariant());
        Assert.Equal("unverified", row.State);
        var output = CaptureConsole(() => GateStatusTextView.Print([], Now, 1, [row]));
        Assert.Contains($"owner={expected} state=unverified", output);
    }

    [Theory]
    [InlineData(-601, true)]
    [InlineData(-600, false)]
    public void Read_FreshnessBoundary_UsesFileWriteTime(int seconds, bool stale)
    {
        using var fixture = new Fixture();
        File.SetLastWriteTimeUtc(fixture.Heartbeat, Now.AddSeconds(seconds).UtcDateTime);
        var row = Assert.Single(fixture.Read());
        Assert.Equal(stale, row.Stale);
        Assert.Equal(stale ? "stale" : "running", row.State);
        Assert.Equal(TimeSpan.FromSeconds(30), row.Age);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("array")]
    [InlineData("wrong-attempt")]
    [InlineData("unknown-outcome")]
    [InlineData("incomplete")]
    public void Read_InvalidMetadata_KeepsUnverifiedIdentity(string kind)
    {
        using var fixture = new Fixture();
        switch (kind)
        {
            case "missing": File.Delete(fixture.Attempt); break;
            case "corrupt": File.WriteAllText(fixture.Attempt, "{"); break;
            case "array": File.WriteAllText(fixture.Attempt, "[]"); break;
            case "wrong-attempt": fixture.Metadata(attemptId: "other"); break;
            case "unknown-outcome": fixture.Metadata(outcome: 999); break;
            case "incomplete": File.WriteAllText(fixture.Attempt, "{\"attemptId\":\"attempt\",\"goalId\":\"goal\"}"); break;
        }
        var row = Assert.Single(fixture.Read());
        Assert.False(row.IdentityVerified);
        Assert.Equal("attempt", row.AttemptId);
        Assert.Equal(GateStatusRemoteWaitDetail.OwnerCheck.Unverified, row.Owner);
        Assert.Equal("unverified", row.State);
        var output = CaptureConsole(() => GateStatusTextView.Print([], Now, 1, [row]));
        Assert.Contains("identity=unverified", output);
        Assert.Contains("owner=unverified", output);
        if (kind is "missing" or "corrupt" or "array" or "wrong-attempt")
            Assert.Contains("owner_pid=unknown candidate=unknown", output);
        if (kind == "unknown-outcome") Assert.Null(row.Outcome);
    }

    [Theory]
    [InlineData("future")]
    [InlineData("invalid")]
    [InlineData("missing")]
    public void Read_InvalidProgress_NeverClampsToHealthyAge(string kind)
    {
        using var fixture = new Fixture();
        fixture.HeartbeatJson(kind == "missing" ? "{\"state\":\"running\"}" :
            JsonSerializer.Serialize(new { state = "running", lastProgressAt = kind == "future" ? Now.AddMinutes(1).ToString("O") : "invalid" }));
        var row = Assert.Single(fixture.Read());
        Assert.Null(row.Age);
        Assert.Equal("unverified", row.State);
        var output = CaptureConsole(() => GateStatusTextView.Print([], Now, 1, [row]));
        Assert.Contains("age=unknown", output);
        Assert.DoesNotContain("state=running", output);
    }

    [Theory]
    [InlineData("starting")]
    [InlineData("exiting")]
    [InlineData("adopted")]
    [InlineData("passed")]
    public void Read_NonRunningHeartbeat_IsExcluded(string state)
    {
        using var fixture = new Fixture();
        fixture.HeartbeatJson(JsonSerializer.Serialize(new { state, lastProgressAt = Now }));
        Assert.Empty(fixture.Read());
    }

    [Fact]
    public void Read_PairingDepthAndGateExclusion_IsolatesEachAttempt()
    {
        using var fixture = new Fixture();
        File.Move(fixture.Attempt, Path.Combine(Path.GetDirectoryName(fixture.Attempt)!, "other.attempt.json"));
        File.Copy(fixture.Heartbeat, Path.Combine(Path.GetDirectoryName(fixture.Heartbeat)!, "attempt.gate-heartbeat.json"));
        var nested = Path.Combine(fixture.Root, "goal", "too-deep");
        Directory.CreateDirectory(nested);
        File.Copy(fixture.Heartbeat, Path.Combine(nested, "hidden.heartbeat.json"));
        File.WriteAllText(Path.Combine(fixture.Root, "bad.heartbeat.json"), "{");
        var row = Assert.Single(fixture.Read());
        Assert.Equal("attempt", row.AttemptId);
        Assert.False(row.IdentityVerified);
        Assert.Empty(GateStatusRemoteWaitDetail.Read(Path.Combine(fixture.Root, "missing"), Now, Window));
    }

    [Fact]
    public void Read_MultipleAttempts_HasStableIdentityOrder()
    {
        using var fixture = new Fixture();
        var folder = Path.GetDirectoryName(fixture.Heartbeat)!;
        File.Copy(fixture.Heartbeat, Path.Combine(folder, "aaa.heartbeat.json"));
        File.WriteAllText(Path.Combine(folder, "aaa.attempt.json"),
            File.ReadAllText(fixture.Attempt).Replace("\"attempt\"", "\"aaa\"", StringComparison.Ordinal));
        Assert.Equal(new[] { "aaa", "attempt" }, fixture.Read().Select(row => row.AttemptId));
    }

    [Fact]
    public void Read_CaseInsensitiveFields_AcceptsProductionAndLegacyCasing()
    {
        using var fixture = new Fixture();
        fixture.HeartbeatJson("{\"State\":\"RUNNING\",\"LastProgressAt\":\"2026-10-17T11:59:30Z\"}");
        File.WriteAllText(fixture.Attempt,
            "{\"AttemptId\":\"attempt\",\"GoalId\":\"abcdef12abcdef12\",\"OwnerProcessId\":123,\"BranchHeadSha\":\"candidate-sha\",\"OwnerProcessStartedAt\":\"2026-10-17T11:00:00Z\",\"Outcome\":\"Running\"}");
        Assert.Equal("running", Assert.Single(fixture.Read()).State);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();
        internal string Heartbeat => Path.Combine(Root, "goal", "attempt.heartbeat.json");
        internal string Attempt => Path.Combine(Root, "goal", "attempt.attempt.json");

        internal Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Heartbeat)!);
            HeartbeatJson(JsonSerializer.Serialize(new { state = "running", lastProgressAt = Now.AddSeconds(-30) }));
            Metadata();
        }

        internal void HeartbeatJson(string json)
        {
            File.WriteAllText(Heartbeat, json);
            File.SetLastWriteTimeUtc(Heartbeat, Now.UtcDateTime);
        }

        internal void Metadata(object? outcome = null, bool withStart = true, string attemptId = "attempt") =>
            File.WriteAllText(Attempt, JsonSerializer.Serialize(new
            {
                attemptId, goalId = "abcdef12abcdef12", ownerProcessId = 123,
                branchHeadSha = "candidate-sha", outcome = outcome ?? 0,
                ownerProcessStartedAt = withStart ? (DateTimeOffset?)Now.AddHours(-1) : null
            }));

        internal IReadOnlyList<GateStatusRemoteWaitDetail.Row> Read() =>
            GateStatusRemoteWaitDetail.Read(Root, Now, Window, pid =>
            {
                Assert.Equal(123, pid);
                return Now.AddHours(-1);
            }, _ => false);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
