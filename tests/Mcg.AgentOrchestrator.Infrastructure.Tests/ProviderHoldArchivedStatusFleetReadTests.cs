using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class ProviderHoldArchivedStatusFleetReadTests
{
    private static readonly DateTimeOffset Now = ProviderHoldSeedFixture.At.AddHours(36);
    private static readonly DateTimeOffset ResetAt = ProviderHoldSeedFixture.At.AddDays(2);
    private static readonly string ResetGoal = ProviderHoldSeedFixture.Id(805);
    private static readonly string RecoveryGoal = ProviderHoldSeedFixture.Id(806);
    private static readonly string EmptyGoal = ProviderHoldSeedFixture.Id(1000);
    private const string ResetIntent = "archive-evidence-reset";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndFleet_MatchingEvidenceSkipsAllArchivePayloadReads(bool compress)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var workspace = await SetUpAsync(fixture, token);
        var statusBaseline = ReadStatus(workspace);
        var fleetBaseline = ReadFleet(workspace);
        AssertBaseline(statusBaseline);
        AssertBaseline(fleetBaseline);
        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now,
            cancellationToken: token, compressSnapshots: compress));

        using var connection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.QueryOnlyRead);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA data_version";
        var before = await version.ExecuteScalarAsync(token);
        var statusReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(statusBaseline, ReadStatus(workspace, statusReads.Add));
        AssertStoredReplay(statusReads);
        var fleetReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(fleetBaseline, ReadFleet(workspace, fleetReads.Add));
        AssertStoredReplay(fleetReads);
        Assert.Equal(before, await version.ExecuteScalarAsync(token));
    }

    [Theory]
    [InlineData("stale", false)]
    [InlineData("wrong-format", true)]
    [InlineData("wrong-goal", false)]
    [InlineData("legacy-absent", true)]
    public async Task StatusAndFleet_RejectedEvidenceReadsOnlyThatArchiveAndPreservesReplay(string defect, bool damageRecovery)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var workspace = await SetUpAsync(fixture, token);
        var statusBaseline = ReadStatus(workspace);
        var fleetBaseline = ReadFleet(workspace);
        AssertBaseline(statusBaseline);
        AssertBaseline(fleetBaseline);
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database);
        var baseline = await repository.ReadProviderHoldStatusHistoryAsync(token);
        Assert.Contains(baseline.Recoveries, attempt => attempt.GoalId == RecoveryGoal);
        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now,
            cancellationToken: token, compressSnapshots: damageRecovery));

        var damagedGoal = damageRecovery ? RecoveryGoal : ResetGoal;
        using var connection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", damagedGoal);
        command.CommandText = defect switch
        {
            "stale" => "UPDATE provider_hold_seed_evidence SET marker = marker || '|stale' WHERE goal_id = $id",
            "wrong-format" => "UPDATE provider_hold_seed_evidence SET format_version = 999 WHERE goal_id = $id",
            "wrong-goal" => "UPDATE provider_hold_seed_evidence SET evidence_json = $payload WHERE goal_id = $id",
            "legacy-absent" => "DELETE FROM provider_hold_seed_evidence WHERE goal_id = $id",
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        if (defect == "wrong-goal")
            command.Parameters.AddWithValue("$payload", GoalSnapshotArchiveCodec.Encode("""{"GoalId":"wrong","Tasks":[],"Timeline":[]}"""));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        command.CommandText = """
            SELECT quote(marker) || ':' || format_version || ':' || quote(evidence_json)
            FROM provider_hold_seed_evidence WHERE goal_id = $id
            """;
        var before = await command.ExecuteScalarAsync(token);

        var statusReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(statusBaseline, ReadStatus(workspace, statusReads.Add));
        AssertPayloadFallback(statusReads, damagedGoal);
        var fleetReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(fleetBaseline, ReadFleet(workspace, fleetReads.Add));
        AssertPayloadFallback(fleetReads, damagedGoal);
        var actual = await repository.ReadProviderHoldStatusHistoryAsync(token);
        Assert.Equal(baseline.Attempts.ToArray(), actual.Attempts.ToArray());
        Assert.Equal(baseline.Recoveries.ToArray(), actual.Recoveries.ToArray());
        Assert.Equal(before, await command.ExecuteScalarAsync(token));
    }

    private static async Task<OrchestratorWorkspace> SetUpAsync(ProviderHoldSeedFixture fixture, CancellationToken token)
    {
        await fixture.PopulateResetCaseAsync(legacyArchive: null, archiveRecovery: true);
        var reset = ProviderHoldSeedFixture.MakeUnrelated(805);
        var task = Assert.Single(reset.Tasks);
        var note = ProviderBudgetHoldSchedules.FormatReset(new("anthropic::c:/profiles/held", ResetAt, ResetIntent));
        reset = reset with
        {
            Tasks = [task with { Status = WorkTaskStatus.Cancelled }],
            Timeline = [new(reset.Id, task.Id, ProgressKind.TaskNote, note, ProviderHoldSeedFixture.At.AddDays(1))]
        };
        var unrelated = ProviderHoldSeedFixture.MakeUnrelated(1000);
        unrelated = unrelated with
        {
            Tasks = [Assert.Single(unrelated.Tasks) with { Status = WorkTaskStatus.Cancelled }],
            Timeline = [new(unrelated.Id, null, ProgressKind.TaskNote, "Unrelated outcome.", ProviderHoldSeedFixture.At.AddDays(1))]
        };
        await fixture.Repository.SaveGoalSnapshotsAsync([reset, unrelated], token);
        // Require replay to discover the successful recovery from its task history.
        using var connection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM model_fit_history WHERE goal_id = $id";
        command.Parameters.AddWithValue("$id", RecoveryGoal);
        await command.ExecuteNonQueryAsync(token);
        return OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
    }

    private static ProviderUsageHoldRow[] ReadStatus(OrchestratorWorkspace workspace,
        Action<GoalSnapshotReadObservation>? observe = null)
    {
        var snapshot = ConductorStatusSnapshotReader.Read(workspace, owner: null, bootTime: Now,
            now: Now, fleetSummaryExpected: false, readObserver: observe);
        Assert.Equal("stopped", snapshot.ConductorState);
        return Assert.IsAssignableFrom<IReadOnlyList<ProviderUsageHoldRow>>(snapshot.ProviderHolds).ToArray();
    }

    private static ProviderUsageHoldRow[] ReadFleet(OrchestratorWorkspace workspace,
        Action<GoalSnapshotReadObservation>? observe = null) => ProviderUsageHoldReader.ReadWorkspace(workspace, Now, observe).ToArray();

    private static void AssertBaseline(ProviderUsageHoldRow[] rows)
    {
        var row = Assert.Single(rows);
        Assert.Equal(ProviderHoldSeedFixture.Id(799), row.Hold.SourceGoalId.Value);
        Assert.Equal(ResetIntent, row.Schedule?.ResetIntentId);
        Assert.Equal(ResetAt, row.Schedule?.NextProbeAt);
    }

    private static void AssertStoredReplay(List<GoalSnapshotReadObservation> reads)
    {
        Assert.Contains(reads, read => read.GoalId == ResetGoal && read.Operation == "provider-seed-archive-projection");
        Assert.Contains(reads, read => read.GoalId == RecoveryGoal && read.Operation == "provider-seed-archive-projection");
        Assert.DoesNotContain(reads, IsArchivePayloadRead);
    }

    private static void AssertPayloadFallback(List<GoalSnapshotReadObservation> reads, string damagedGoal)
    {
        Assert.Contains(reads, read => read.GoalId == damagedGoal && read.Operation == "provider-seed-discovery" &&
            read.MaterializedCharacters > 0);
        Assert.DoesNotContain(reads, read => read.GoalId != damagedGoal && IsArchivePayloadRead(read));
    }

    private static bool IsArchivePayloadRead(GoalSnapshotReadObservation read) =>
        (read.GoalId == ResetGoal || read.GoalId == RecoveryGoal || read.GoalId == EmptyGoal) &&
        read.Operation is "provider-seed-discovery" or "provider-seed-snapshot-decode" or "archive-attempt" or "archive-deserialized";
}
