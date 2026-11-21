using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private workspace/database, injected status probes and local writers.
public sealed class ConductorStatusMainFullVerificationTests
{
    [Fact]
    public void LatestCompletedAttemptShowsCommitTimeDurationAndResultOnBothSurfaces()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var path = fixture.Workspace.RunEventStorePath;
        _ = new SqliteRunEventStore(path);
        var store = new MainFullVerificationStore(path);
        var started = new DateTimeOffset(2026, 11, 21, 10, 1, 0, TimeSpan.Zero);
        var completed = started.AddSeconds(42);
        var older = new MainFullVerificationRecord("older-passing-main", started.AddDays(-1), 21600,
            started.AddDays(-1), completed.AddDays(-1), 42, new("passed"));
        var latest = new MainFullVerificationRecord("latest-failing-main", started.AddMinutes(-1), 21600,
            started, completed, 42, new("failed", Failures: [new("Example.Tests.Fails")]));
        store.Write("completed", older, older.CompletedAt!.Value);
        store.Write("completed", latest, completed);
        store.Write("admitted", new("outstanding-main", completed.AddHours(6), 21600), completed.AddHours(6));

        Assert.Contains("Main full verification: latest-failing-main result=failed completed=2026-11-21T10:01:42.0000000+00:00 duration=42s",
            ReadStatus(fixture.Workspace, json: false));
        using var document = JsonDocument.Parse(ReadStatus(fixture.Workspace, json: true));
        var result = document.RootElement.GetProperty("payload").GetProperty("mainFullVerification");
        Assert.Equal("latest-failing-main", result.GetProperty("commit").GetString());
        Assert.Equal("failed", result.GetProperty("result").GetString());
        Assert.Equal(started, result.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(completed, result.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(42d, result.GetProperty("durationSeconds").GetDouble());
    }

    private static string ReadStatus(OrchestratorWorkspace workspace, bool json)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(null), mainCommit: () => null,
            output: output, error: error));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }
}
