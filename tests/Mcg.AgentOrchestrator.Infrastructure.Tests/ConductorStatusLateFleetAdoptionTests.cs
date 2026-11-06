using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: only private conduct logs; deadline assertions use an injected fixed clock.
public sealed class ConductorStatusLateFleetAdoptionTests
{
    private const string Commit = "abcdef0123456789abcdef0123456789abcdef0123";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(901, true)]
    [InlineData(960, true)]
    [InlineData(900, false)]
    [InlineData(899, false)]
    [InlineData(-60, false)]
    public void LateLineRequiresStrictlyMoreThanFifteenMinutes(int ageSeconds, bool expected)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Adopted(Now.AddSeconds(-ageSeconds)));

        var snapshot = Read(fixture.Workspace);
        var lines = Human(snapshot).Split(Environment.NewLine)
            .Where(line => line.StartsWith("Fleet adoption late:", StringComparison.Ordinal)).ToArray();

        if (expected)
        {
            Assert.Equal(new(Commit, Now.AddSeconds(-ageSeconds), TimeSpan.FromSeconds(ageSeconds)), snapshot.LateFleetAdoption);
            Assert.Equal($"Fleet adoption late: commit={Commit} elapsed={ageSeconds / 60}m since ACTIVATION_ADOPTED with no FLEET_BUILD_ADOPTED; pending projects unavailable", Assert.Single(lines));
        }
        else
        {
            Assert.Null(snapshot.LateFleetAdoption);
            Assert.Empty(lines);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SummaryMatchesFullCommitAndMustFollowAdoption(bool follows, bool matches)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var adoption = Adopted(Now.AddMinutes(-20));
        // Deliberately misleading timestamps: append order owns correlation.
        var summary = Event(Now.AddHours(-1), "FLEET_BUILD_ADOPTED commit=" +
            (matches ? Commit.ToUpperInvariant() : Commit[..8]) + " adopted=home pending= failed= elapsedSeconds=300");
        Write(fixture.Workspace, follows ? [adoption, summary] : [summary, adoption]);

        var snapshot = Read(fixture.Workspace);

        if (follows && matches)
        {
            Assert.Null(snapshot.LateFleetAdoption);
            Assert.DoesNotContain("Fleet adoption late:", Human(snapshot));
        }
        else
        {
            Assert.Equal(Commit, snapshot.LateFleetAdoption!.Commit);
            Assert.Contains("Fleet adoption late:", Human(snapshot));
        }
    }

    [Fact]
    public void OnlyNewestAdoptionIsEvaluated()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Adopted(Now.AddHours(-1)), Adopted(Now.AddMinutes(-1)));

        Assert.Null(Read(fixture.Workspace).LateFleetAdoption);

        Write(fixture.Workspace, Adopted(Now.AddHours(-1)),
            Event(Now.AddMinutes(-30), "FLEET_BUILD_ADOPTED commit=" + Commit),
            Adopted(Now.AddMinutes(-20)));
        Assert.Equal(TimeSpan.FromMinutes(20), Read(fixture.Workspace).LateFleetAdoption!.Elapsed);
    }

    [Theory]
    [InlineData("ACTIVATION_REVERTED restoredCommit=previous")]
    [InlineData("ACTIVATION_SUPPRESSED currentCommit=previous")]
    public void LaterLoopDecisionAndSupervisorDoNotHideMissingSummary(string decision)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Adopted(Now.AddMinutes(-20)), Event(Now.AddMinutes(-10), decision),
            JsonSerializer.Serialize(new { timestamp = Now, eventKind = "supervisor-build",
                detail = "SUPERVISOR_BUILD {\"commitSha\":\"previous\"}" }));

        var snapshot = Read(fixture.Workspace);

        Assert.Equal("previous", snapshot.LoopBuild!.Commit);
        Assert.Equal(Commit, snapshot.LateFleetAdoption!.Commit);
    }

    [Fact]
    public void FollowerWorkspaceDoesNotExpectFleetSummary()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Adopted(Now.AddMinutes(-20)));

        Assert.Null(Read(fixture.Workspace, fleetSummaryExpected: false).LateFleetAdoption);
        Assert.NotNull(Read(fixture.Workspace).LateFleetAdoption);
    }

    [Fact]
    public void RotatedAdoptionCanBeSatisfiedByLiveSummary()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace);
        File.WriteAllLines(Path.Combine(fixture.Workspace.LogDirectory, "conduct-events-20261105070000.log"),
            [Adopted(Now.AddMinutes(-20))]);

        Assert.Equal(Commit, Read(fixture.Workspace).LateFleetAdoption!.Commit);

        Write(fixture.Workspace, Event(Now, "FLEET_BUILD_ADOPTED commit=" + Commit));
        Assert.Null(Read(fixture.Workspace).LateFleetAdoption);
    }

    [Theory]
    [InlineData("ACTIVATION_ADOPTED")]
    [InlineData("ACTIVATION_ADOPTED adoptedCommit=none")]
    public void MissingNewestCommitDoesNotReuseOlderAdoption(string detail)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Write(fixture.Workspace, Adopted(Now.AddHours(-1)), Event(Now.AddMinutes(-20), detail));

        Assert.Null(Read(fixture.Workspace).LateFleetAdoption);
    }

    private static ConductorStatusSnapshot Read(OrchestratorWorkspace workspace, bool fleetSummaryExpected = true) =>
        ConductorStatusSnapshotReader.Read(workspace, null, Now.AddDays(-1), () => Commit,
            now: Now, fleetSummaryExpected: fleetSummaryExpected);
    private static string Human(ConductorStatusSnapshot snapshot)
    {
        using var writer = new StringWriter();
        CliConductorStatusReader.Print(snapshot, writer);
        return writer.ToString();
    }
    private static string Adopted(DateTimeOffset at) => Event(at, "ACTIVATION_ADOPTED adoptedCommit=" + Commit);
    private static string Event(DateTimeOffset at, string detail) =>
        JsonSerializer.Serialize(new { timestamp = at, eventKind = "loop-handoff", detail });
    private static void Write(OrchestratorWorkspace workspace, params string[] events)
    {
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllLines(workspace.ConductEventsLogPath, events);
    }
}
