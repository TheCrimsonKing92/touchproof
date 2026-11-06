using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: private workspace files, injected main commit and clock; no live processes.
public sealed class ConductorStatusLoopBuildTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccccccccccc";
    private static readonly DateTimeOffset At = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(B, "current")]
    [InlineData(C, "differs")]
    public void AdoptedBuildDrivesGeneration(string main, string state)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Build(A), Activation("ACTIVATION_ADOPTED adoptedCommit=" + B));

        var snapshot = Read(fixture.Workspace, main);

        Assert.Equal(A, snapshot.SupervisorBuild);
        Assert.Equal(new(B, "ACTIVATION_ADOPTED"), snapshot.LoopBuild);
        Assert.Equal(new(state, B, main), snapshot.Generation);
        Assert.Contains($"Generation: {(state == "current" ? "current with main" : "differs from main")} (running {B}, main {main})", Human(snapshot));
    }

    [Theory]
    [InlineData("ACTIVATION_REVERTED failedCommit=" + B + " restoredCommit=" + A, "ACTIVATION_REVERTED")]
    [InlineData("ACTIVATION_SUPPRESSED suppressedCommit=" + B + " currentCommit=" + A, "ACTIVATION_SUPPRESSED")]
    public void LatestDecisionOverridesHandoffReadiness(string decision, string source)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Build(A), Activation("ACTIVATION_ADOPTED adoptedCommit=" + B),
            Activation("LOOP_HANDOFF stagedSourceCommit=" + B), Activation(decision));

        var snapshot = Read(fixture.Workspace, A);

        Assert.Equal(new(A, source), snapshot.LoopBuild);
        Assert.Equal(new("current", A, A), snapshot.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupervisorBaselineDiscardsEarlierDecisions(bool unadoptedHandoff)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var events = new List<string> { Activation("ACTIVATION_ADOPTED adoptedCommit=" + B), Build(A) };
        if (unadoptedHandoff) events.Add(Activation("LOOP_HANDOFF stagedSourceCommit=" + B));
        Write(fixture.Workspace, events.ToArray());

        var snapshot = Read(fixture.Workspace, A);

        Assert.Equal(A, snapshot.SupervisorBuild);
        Assert.Equal(new(A, "supervisor"), snapshot.LoopBuild);
        Assert.Contains($"Loop build: {A} (supervisor build; no activation since supervisor start)", Human(snapshot));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" adoptedCommit=")]
    [InlineData(" adoptedCommit=none")]
    public void MalformedLatestDecisionDoesNotReuseAnOlderBuild(string commitToken)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Build(A), Activation("ACTIVATION_ADOPTED adoptedCommit=" + B),
            Activation("ACTIVATION_ADOPTED" + commitToken));

        var snapshot = ConductorStatusSnapshotReader.Read(fixture.Workspace, null, At,
            mainCommit: () => throw new InvalidOperationException("Main must not be read without a loop commit"),
            now: At, fleetSummaryExpected: false);

        Assert.Equal(A, snapshot.SupervisorBuild);
        Assert.Null(snapshot.LoopBuild!.Commit);
        Assert.Equal("ACTIVATION_ADOPTED", snapshot.LoopBuild.Source);
        Assert.Equal(new("loop-unavailable", null, null), snapshot.Generation);
        Assert.Contains("Loop build: unavailable", Human(snapshot));
        Assert.Contains("Generation: unavailable (loop build unreadable)", Human(snapshot));
    }

    [Fact]
    public void DecisionWithoutSupervisorStillIdentifiesTheLoop()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Activation("ACTIVATION_ADOPTED adoptedCommit=" + B));

        var snapshot = Read(fixture.Workspace, B);

        Assert.Null(snapshot.SupervisorBuild);
        Assert.Equal(new(B, "ACTIVATION_ADOPTED"), snapshot.LoopBuild);
        Assert.Equal(new("current", B, B), snapshot.Generation);
    }

    [Fact]
    public void DecisionsUseAppendOrderDespiteTimestampSkew()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Build(A),
            Event(At.AddHours(1), "loop-handoff", "ACTIVATION_ADOPTED adoptedCommit=" + B),
            Event(At.AddMinutes(-1), "loop-handoff", "ACTIVATION_REVERTED restoredCommit=" + A));

        Assert.Equal(new(A, "ACTIVATION_REVERTED"), Read(fixture.Workspace, A).LoopBuild);
    }

    [Fact]
    public void LiveDecisionUsesRotatedSupervisorAndIgnoresOlderHistory()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Activation("ACTIVATION_SUPPRESSED currentCommit=" + B));
        File.WriteAllLines(Path.Combine(fixture.Workspace.LogDirectory, "conduct-events-20261105080000.log"),
            [Activation("ACTIVATION_ADOPTED adoptedCommit=" + C), Build(A)]);

        Assert.Equal(A, Read(fixture.Workspace, B).SupervisorBuild);
        Assert.Equal(new(B, "ACTIVATION_SUPPRESSED"), Read(fixture.Workspace, B).LoopBuild);

        Write(fixture.Workspace, Build(C));
        Assert.Equal(new(C, "supervisor"), Read(fixture.Workspace, C).LoopBuild);

        Write(fixture.Workspace);
        File.AppendAllLines(Path.Combine(fixture.Workspace.LogDirectory, "conduct-events-20261105080000.log"),
            [Activation("ACTIVATION_ADOPTED adoptedCommit=" + B)]);
        Assert.Equal(new(B, "ACTIVATION_ADOPTED"), Read(fixture.Workspace, B).LoopBuild);
    }

    [Fact]
    public void HumanJsonAndConsoleExposeDistinctBuilds()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Build(A), Activation("ACTIVATION_ADOPTED adoptedCommit=" + B));
        using var output = new StringWriter();
        using var error = new StringWriter();
        ConductorStatusSnapshot? observed = null;
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status", "--json"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => At,
            mainCommit: () => B, output: output, error: error, observeStatus: snapshot => observed = snapshot));
        Assert.Equal("", error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(A, payload.GetProperty("supervisorBuild").GetString());
        Assert.Equal(B, payload.GetProperty("loopBuild").GetProperty("commit").GetString());
        Assert.Equal("ACTIVATION_ADOPTED", payload.GetProperty("loopBuild").GetProperty("source").GetString());
        Assert.NotNull(observed);
        output.GetStringBuilder().Clear();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => At,
            mainCommit: () => B, output: output, error: error));
        Assert.Equal("", error.ToString());
        Assert.Contains($"Supervisor build: {A}", output.ToString());
        Assert.Contains($"Loop build: {B} (ACTIVATION_ADOPTED)", output.ToString());
        var fields = CliConductorConsoleAdapter.StatusFields(observed);
        Assert.Equal(A, Assert.Single(fields.Fields.Where(field => field.Key == "build")).Value);
        Assert.Equal(B, Assert.Single(fields.Fields.Where(field => field.Key == "loop-build")).Value);
        Assert.Equal("ACTIVATION_ADOPTED", Assert.Single(fields.Fields.Where(field => field.Key == "loop-build/source")).Value);
    }

    private static ConductorStatusSnapshot Read(OrchestratorWorkspace workspace, string main) =>
        ConductorStatusSnapshotReader.Read(workspace, null, At, () => main, now: At, fleetSummaryExpected: false);

    private static string Human(ConductorStatusSnapshot snapshot)
    {
        using var writer = new StringWriter();
        CliConductorStatusReader.Print(snapshot, writer);
        return writer.ToString();
    }

    private static string Build(string commit) => Event(At, "supervisor-build",
        "SUPERVISOR_BUILD " + JsonSerializer.Serialize(new { commitSha = commit }));
    private static string Activation(string detail) => Event(At, "loop-handoff", detail);
    private static string Event(DateTimeOffset at, string kind, string detail) =>
        JsonSerializer.Serialize(new { timestamp = at, eventKind = kind, detail });
    private static void Write(OrchestratorWorkspace workspace, params string[] events)
    {
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllLines(workspace.ConductEventsLogPath, events);
    }
}
