using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class ProviderHoldArchiveEvidenceWriterTests
{
    private static readonly DateTimeOffset Cutoff = ProviderHoldSeedFixture.At.AddDays(20);
    private static readonly JsonSerializerOptions Options = SqliteOrchestratorStateRepository.HistorySerializerOptions;

    [Theory]
    [InlineData(false, "text")]
    [InlineData(true, "blob")]
    public async Task Archive_WritesCurrentCanonicalEvidenceForHoldAndEmptyGoals(bool compress, string storageType)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var (hold, empty) = await SaveGoalsAsync(fixture, token);
        var ids = new GoalId[] { new(hold), new(empty) };
        await using var readerConnection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.QueryOnlyRead);
        var canonical = await ProviderHoldSeedEvidenceReader.ReadSnapshotsAsync(readerConnection, ids, Options, token);
        Assert.True(ProviderHoldSeedDiscovery.IsRefusal(Assert.Single(canonical.Single(row => row.GoalId == hold).Tasks).LastVerification));
        Assert.Single(canonical.Single(row => row.GoalId == hold).Timeline);
        var noHoldEvidence = canonical.Single(row => row.GoalId == empty);
        var noHoldTask = Assert.Single(noHoldEvidence.Tasks);
        Assert.Null(noHoldTask.LastDispatch);
        Assert.Empty(noHoldTask.DispatchHistory);
        Assert.Null(noHoldTask.LastVerification);
        Assert.Empty(noHoldTask.VerificationHistory);
        Assert.Empty(noHoldEvidence.Timeline);

        Assert.Equal(2, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff,
            cancellationToken: token, compressSnapshots: compress));
        await using (var command = readerConnection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT g.id, e.format_version, e.marker, {ProviderHoldSeedEvidenceStore.MarkerSql(hasArchive: true)},
                    typeof(a.snapshot_json)
                FROM goals g JOIN goal_snapshot_archive a ON a.goal_id = g.id
                JOIN provider_hold_seed_evidence e ON e.goal_id = g.id ORDER BY g.id
                """;
            await using var rows = await command.ExecuteReaderAsync(token);
            foreach (var id in ids)
            {
                Assert.True(await rows.ReadAsync(token));
                Assert.Equal(id.Value, rows.GetString(0));
                Assert.Equal(ProviderHoldSeedEvidenceStore.FormatVersion, rows.GetInt32(1));
                Assert.Equal(rows.GetString(3), rows.GetString(2));
                Assert.Equal(storageType, rows.GetString(4));
            }
            Assert.False(await rows.ReadAsync(token));
        }

        var reads = new List<GoalSnapshotReadObservation>();
        var stored = await ProviderHoldSeedEvidenceStore.ReadAsync(readerConnection, ids, Options, token, reads.Add);
        Assert.Empty(reads);
        Assert.Equal(canonical.Select(row => JsonSerializer.Serialize(row, Options)),
            stored.Select(row => JsonSerializer.Serialize(row, Options)));
    }

    [Fact]
    public async Task Archive_RollbackDiscardsArchiveAndEvidenceRows()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        await SaveGoalsAsync(fixture, token);
        // Initialize the tables outside the transaction so both remain queryable after rollback.
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(ProviderHoldSeedFixture.At,
            cancellationToken: token));
        await using var connection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        await ProviderHoldSeedEvidenceStore.CreateSchemaAsync(connection, token);
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        await command.ExecuteNonQueryAsync(token);
        try
        {
            Assert.Equal(2, await new GoalSnapshotArchive(Options).ArchiveAsync(connection, Cutoff,
                cancellationToken: token));
            await AssertCountsAsync(2L);
        }
        finally
        {
            command.CommandText = "ROLLBACK";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        await AssertCountsAsync(0L);

        async Task AssertCountsAsync(long count)
        {
            command.CommandText = "SELECT count(*) FROM goal_snapshot_archive";
            Assert.Equal(count, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT count(*) FROM provider_hold_seed_evidence";
            Assert.Equal(count, await command.ExecuteScalarAsync(token));
        }
    }

    [Fact]
    public async Task Archive_ProjectionFailureRollsBackArchiveAndEvidenceRows()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var (_, empty) = await SaveGoalsAsync(fixture, token);
        Assert.Equal(0, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(ProviderHoldSeedFixture.At,
            cancellationToken: token));
        await using var connection = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        await ProviderHoldSeedEvidenceStore.CreateSchemaAsync(connection, token);
        await using var command = connection.CreateCommand();
        // Keep the replacement inline so sidecar hydration succeeds before the projector checks the ID.
        var mismatched = ProviderHoldSeedFixture.MakeUnrelated(3);
        var task = Assert.Single(mismatched.Tasks) with { Status = WorkTaskStatus.Cancelled };
        var payload = JsonSerializer.Serialize(mismatched with { Tasks = [task] }, Options);
        command.CommandText = "UPDATE goals SET snapshot_json = $payload WHERE id = $id";
        command.Parameters.AddWithValue("$id", empty);
        command.Parameters.AddWithValue("$payload", payload);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Cutoff, cancellationToken: token));
        Assert.Equal("Provider seed evidence goal identity mismatch.", failure.Message);
        command.Parameters.Clear();
        command.CommandText = "SELECT count(*) FROM goal_snapshot_archive";
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT count(*) FROM provider_hold_seed_evidence";
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
    }

    private static async Task<(string Hold, string Empty)> SaveGoalsAsync(ProviderHoldSeedFixture fixture, CancellationToken token)
    {
        var hold = ProviderHoldSeedFixture.MakeUnrelated(1);
        var task = Assert.Single(hold.Tasks);
        var refusal = task.LastVerification! with
        {
            StandardError = "ERROR: weekly usage limit reached",
            ProviderFailureKind = ProviderFailureKind.BudgetExhausted
        };
        var terminal = Assert.Single(hold.Timeline);
        var reset = ProviderBudgetHoldSchedules.FormatReset(new("anthropic::c:/profiles/held", Cutoff, "archive-reset"));
        hold = hold with
        {
            Tasks = [task with { Status = WorkTaskStatus.Completed, LastVerification = refusal, VerificationHistory = [refusal] }],
            Timeline = [terminal with { Message = reset }]
        };
        var empty = ProviderHoldSeedFixture.MakeUnrelated(2);
        var emptyTask = Assert.Single(empty.Tasks) with
        {
            Status = WorkTaskStatus.Cancelled,
            LastDispatch = null,
            DispatchHistory = [],
            LastVerification = null,
            VerificationHistory = []
        };
        empty = empty with { Tasks = [emptyTask] };
        await fixture.Repository.SaveGoalSnapshotsAsync([hold, empty], token);
        return (hold.Id, empty.Id);
    }
}
