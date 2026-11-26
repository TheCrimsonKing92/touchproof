using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using Microsoft.Data.Sqlite;

// Parallel-safe. Shadow startup owns the database copy, candidate process group and stop file.
public sealed class ProviderHoldSeedConductorStartupTests(ITestOutputHelper testOutput)
{
    private const string SuccessfulSeed =
        "STARTUP_COMPLETE phase=provider-hold-history-seed elapsed_ms=123 source_goals=800 evidence_goals=803 indexed_goals=803 binding_evaluations=8 observation_visits=3108";

    [Fact]
    public async Task ShadowStart_PersistedDispatchHistory_CompletesSeedBeforeReadyAndLoop()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAsync();
        var dispatchGoals = fixture.CountDispatchGoals();
        Assert.Equal(3200, dispatchGoals);

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var candidate = typeof(CliShadowStartCommand).Assembly.Location;
        Assert.True(File.Exists(candidate), $"Missing candidate assembly {candidate}");
        var exit = CliShadowStartCommand.Run(
            ["shadow-start", "--source", fixture.Source, "--scratch", fixture.Scratch,
             "--startup-ceiling-seconds", "60", "--budget-seconds", "120"], output, error);

        Assert.True(exit == 0, $"Candidate {candidate} exited {exit}{Environment.NewLine}{error}");
        var receipt = output.ToString();
        var rejection = Reject(exit, receipt);
        Assert.True(rejection is null, $"Rejected {rejection}{Environment.NewLine}{receipt}");
        testOutput.WriteLine($"Candidate={candidate} fixture_dispatch_goals={dispatchGoals}{Environment.NewLine}{receipt}");
    }

    [Fact]
    public async Task ShadowStart_InvalidNonReplayTimelineEntry_FailsBeforeReadyAndLoop()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateRecoveredRefusalAsync();
        // Initial state hydration reads only the archived goal's stub. Recovery clears its
        // hold, so discovering the refusal is the only reason the seed needs its timeline.
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(
            ProviderHoldSeedFixture.At.AddDays(1), cancellationToken: TestContext.Current.CancellationToken));
        var goalId = ProviderHoldSeedFixture.Id(0);
        using (var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE goal_timeline SET entry_json = json_set(entry_json,
                    '$.Kind', 'GoalPolicyDecision', '$.OccurredAt', 'invalid date')
                WHERE goal_id = $id AND sequence = 0 AND EXISTS (
                    SELECT 1 FROM goal_snapshot_archive
                    WHERE goal_id = $id AND json_extract(snapshot_json, '$.TimelineInTable') = 1)
                """;
            command.Parameters.AddWithValue("$id", goalId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        AssertTimelineFailure(fixture, goalId, "unparseable entry at 0");
    }

    [Theory]
    [InlineData(true, "expected 1 rows, found 0")]
    [InlineData(false, "unparseable entry at 0")]
    public async Task ShadowStart_InvalidRecoveryArchiveTimeline_FailsBeforeReadyAndLoop(
        bool missingRow, string defect)
    {
        using var fixture = new ProviderHoldSeedFixture();
        // The archived success has no indexed outcome or provider marker. Its task history
        // nominates it as a recovery witness after the separate refusal is discovered.
        await fixture.PopulateArchivedRecoveryTimelineAsync(1);
        var goalId = ProviderHoldSeedFixture.Id(805);
        using (var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = missingRow
                ? "DELETE FROM goal_timeline WHERE goal_id = $id AND sequence = 0"
                : """
                    UPDATE goal_timeline SET entry_json = json_set(entry_json, '$.OccurredAt', 'invalid date')
                    WHERE goal_id = $id AND sequence = 0
                    """;
            command.Parameters.AddWithValue("$id", goalId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        AssertTimelineFailure(fixture, goalId, defect);
    }

    private void AssertTimelineFailure(ProviderHoldSeedFixture fixture, string goalId, string defect)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = CliShadowStartCommand.Run(
            ["shadow-start", "--source", fixture.Source, "--scratch", fixture.Scratch,
             "--startup-ceiling-seconds", "60", "--budget-seconds", "120"], output, error);

        var receipt = output.ToString() + error;
        testOutput.WriteLine(receipt);
        Assert.Contains("SHADOW_START result=fail assertion=loop-start", receipt, StringComparison.Ordinal);
        Assert.Contains("PROVIDER_HOLD_HISTORY seed-failed: InvalidDataException:", receipt, StringComparison.Ordinal);
        Assert.Contains($"Goal '{goalId}' timeline: {defect}.", receipt, StringComparison.Ordinal);
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain(ProviderHoldHistoryStartupSeed.CompletedLinePrefix, receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_READY ", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_START ", receipt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "", "shadow-start")]
    [InlineData(0, "PROVIDER_HOLD_HISTORY seed-failed: IOException: fixture|LOOP_READY fixture|LOOP_START fixture", "seed")]
    [InlineData(0, "SEED|LOOP_START fixture", "readiness")]
    [InlineData(0, "LOOP_READY fixture|SEED|LOOP_START fixture", "readiness")]
    [InlineData(0, "SEED|LOOP_READY fixture", "loop-start")]
    [InlineData(0, "SEED|LOOP_START fixture|LOOP_READY fixture", "loop-start")]
    [InlineData(0, "OVER_BOUND|LOOP_READY fixture|LOOP_START fixture", "work-bound")]
    public void StartupEvidence_IncompleteOrOverBound_Rejects(int exit, string evidence, string expected)
    {
        var lines = evidence.Split('|').Select(line => line switch
        {
            "SEED" => SuccessfulSeed,
            "OVER_BOUND" => SuccessfulSeed.Replace("binding_evaluations=8", "binding_evaluations=9", StringComparison.Ordinal),
            _ => line
        });
        Assert.Equal(expected, Reject(exit, string.Join(Environment.NewLine, lines)));
    }

    private static string? Reject(int exit, string output)
    {
        // Shadow startup supplies the launch-to-first-tick deadline and clean child exit.
        if (exit != 0) return "shadow-start";
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var seed = Array.FindIndex(lines, line => line.StartsWith(
            ProviderHoldHistoryStartupSeed.CompletedLinePrefix, StringComparison.Ordinal));
        if (seed < 0) return "seed";
        var counts = lines[seed][ProviderHoldHistoryStartupSeed.CompletedLinePrefix.Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(field => field.Split('=', 2))
            .Where(field => field.Length == 2).ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
        if (!Count("elapsed_ms", 0, long.MaxValue)) return "seed";
        if (!Count("source_goals", 800, 800) || !Count("evidence_goals", 803, 803) ||
            !Count("indexed_goals", 803, 803) || !Count("binding_evaluations", 8, 8) ||
            !Count("observation_visits", 1, 3212))
            return "work-bound";
        var ready = Array.FindIndex(lines, line => line.StartsWith("LOOP_READY ", StringComparison.Ordinal));
        if (ready <= seed) return "readiness";
        var started = Array.FindIndex(lines, line => line.StartsWith("LOOP_START ", StringComparison.Ordinal));
        return started <= ready ? "loop-start" : null;

        bool Count(string field, long min, long max) =>
            long.TryParse(counts.GetValueOrDefault(field), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count >= min && count <= max;
    }
}
