using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class ProviderHoldIndexStampSnapshotTests
{
    private static readonly DateTimeOffset Now = ProviderHoldSeedFixture.At.AddHours(36);
    private static readonly DateTimeOffset ResetAt = ProviderHoldSeedFixture.At.AddDays(2);
    private static readonly DateTimeOffset LaterCutoff = ProviderHoldSeedFixture.At.AddDays(20);
    private static readonly string ResetGoal = ProviderHoldSeedFixture.Id(805);
    private static readonly string RecoveryGoal = ProviderHoldSeedFixture.Id(806);
    private static readonly string UnrelatedGoal = ProviderHoldSeedFixture.Id(1000);
    private const string ResetIntent = "snapshot-index-reset";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndFleet_MatchingCopy_ReplayWithoutArchivePayloads(bool compress)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var baseline = await PrepareArchiveAsync(fixture, compress, token);
        var workspace = Copy(fixture, token);
        var path = workspace.SqliteStatePath;
        Assert.Equal(ReadValidStamp(fixture.Database, token), ReadValidStamp(path, token));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        using var readOnly = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead);
        var dataVersion = Scalar(readOnly, "PRAGMA data_version");

        await AssertReplayAsync(workspace, baseline, AssertNoPayloads, token);

        Assert.Equal(dataVersion, Scalar(readOnly, "PRAGMA data_version"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData("older")]
    [InlineData("newer")]
    [InlineData("identity")]
    [InlineData("absent")]
    public async Task StatusAndFleet_RejectedStamp_ReplayCanonicalArchivePayloads(string defect)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var baseline = await PrepareArchiveAsync(fixture, compress: true, token);
        var workspace = Copy(fixture, token);
        var path = workspace.SqliteStatePath;
        var matching = ReadValidStamp(path, token);
        using (var writer = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.ReadWrite))
        {
            Execute(writer, defect switch
            {
                "older" => "UPDATE provider_hold_index_stamp SET index_version = index_version - 1",
                "newer" => "UPDATE provider_hold_index_stamp SET index_version = index_version + 1",
                "identity" => "UPDATE provider_hold_index_stamp SET generation = generation + 1",
                "absent" => "DROP TABLE provider_hold_index_stamp",
                _ => throw new ArgumentOutOfRangeException(nameof(defect))
            });
        }

        await AssertReplayAsync(workspace, baseline, reads =>
        {
            foreach (var id in new[] { ResetGoal, RecoveryGoal })
                Assert.Contains(reads, read => read.GoalId == id && read.Operation == "provider-seed-discovery" &&
                    read.MaterializedCharacters > 0);
        }, token);

        using var readOnly = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead);
        var reads = new List<GoalSnapshotReadObservation>();
        var ids = new GoalId[] { new(ResetGoal), new(RecoveryGoal) };
        var options = SqliteOrchestratorStateRepository.HistorySerializerOptions;
        var expected = await ProviderHoldSeedEvidenceReader.ReadSnapshotsAsync(readOnly, ids, options, token);
        var actual = await ProviderHoldSeedEvidenceStore.ReadAsync(readOnly, ids, options, token, reads.Add);
        Assert.Equal(expected.Select(row => JsonSerializer.Serialize(row, options)),
            actual.Select(row => JsonSerializer.Serialize(row, options)));
        foreach (var id in ids)
            Assert.Contains(reads, read => read.GoalId == id.Value && read.Operation == "provider-seed-snapshot-decode");
        Assert.Equal(matching, ReadValidStamp(fixture.Database, token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceWrite_AfterCopy_AdvancesOnlySourceStampAndEvidence(bool archive)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var baseline = await PrepareArchiveAsync(fixture, compress: true, token);
        var goalId = await SaveNextArchiveAsync(fixture, token);
        var workspace = Copy(fixture, token);
        var copy = workspace.SqliteStatePath;
        var before = ReadValidStamp(copy, token);
        var copiedRows = ReadEvidenceRows(copy);
        Assert.Equal(before, ReadValidStamp(fixture.Database, token));
        Assert.DoesNotContain(copiedRows, row => row.StartsWith(goalId, StringComparison.Ordinal));

        if (archive)
        {
            Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(LaterCutoff,
                cancellationToken: token, compressSnapshots: true));
        }
        else
        {
            using var writer = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
            var reads = new List<GoalSnapshotReadObservation>();
            var options = SqliteOrchestratorStateRepository.HistorySerializerOptions;
            Assert.Equal(goalId, Assert.Single(await ProviderHoldSeedEvidenceStore.ReadAsync(
                writer, [new(goalId)], options, token, reads.Add)).GoalId);
            Assert.Contains(reads, read => read.GoalId == goalId && read.Operation == "provider-seed-snapshot-decode");
        }

        Assert.True(ReadValidStamp(fixture.Database, token).Generation > before.Generation);
        Assert.Contains(ReadEvidenceRows(fixture.Database), row => row.StartsWith(goalId, StringComparison.Ordinal));
        Assert.Equal(before, ReadValidStamp(copy, token));
        Assert.Equal(copiedRows, ReadEvidenceRows(copy));
        await AssertReplayAsync(workspace, baseline, AssertNoPayloads, token);
    }

    [Fact]
    public async Task Archive_RollbackRestoresStampHeaderAndEvidence()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        await PrepareArchiveAsync(fixture, compress: true, token);
        await SaveNextArchiveAsync(fixture, token);
        var before = ReadValidStamp(fixture.Database, token);
        var rows = ReadEvidenceRows(fixture.Database);
        using var writer = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        const string archiveCount = "SELECT count(*) FROM goal_snapshot_archive";
        const string evidenceCount = "SELECT count(*) FROM provider_hold_seed_evidence";
        Assert.Equal(3L, Scalar(writer, archiveCount));
        Assert.Equal(3L, Scalar(writer, evidenceCount));

        Execute(writer, "BEGIN IMMEDIATE");
        try
        {
            var archive = new GoalSnapshotArchive(SqliteOrchestratorStateRepository.HistorySerializerOptions);
            Assert.Equal(1, await archive.ArchiveAsync(writer, LaterCutoff, cancellationToken: token));
            var pending = Assert.IsType<ProviderHoldIndexStamp>(ProviderHoldIndexStamp.TryRead(writer, token));
            Assert.True(pending.IsValid);
            Assert.True(pending.Generation > before.Generation);
            Assert.Equal(4L, Scalar(writer, archiveCount));
            Assert.Equal(4L, Scalar(writer, evidenceCount));
            Assert.Equal(before, ReadValidStamp(fixture.Database, token));
            Assert.Equal(rows, ReadEvidenceRows(fixture.Database));
        }
        finally
        {
            Execute(writer, "ROLLBACK");
        }

        Assert.Equal(before, ReadValidStamp(fixture.Database, token));
        Assert.Equal(rows, ReadEvidenceRows(fixture.Database));
        Assert.Equal(3L, Scalar(writer, archiveCount));
        Assert.Equal(3L, Scalar(writer, evidenceCount));
    }

    [Fact]
    public async Task Copy_ConcurrentWalCommit_PreservesPinnedStampAndEvidence()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        var baseline = await PrepareArchiveAsync(fixture, compress: true, token);
        using var writer = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite);
        var goalId = await SaveNextArchiveAsync(fixture, token);
        Assert.True(File.Exists(fixture.Database + "-wal"));
        Assert.True(File.Exists(fixture.Database + "-shm"));
        var before = ReadValidStamp(fixture.Database, token);
        var rows = ReadEvidenceRows(fixture.Database);

        var workspace = Copy(fixture, token, beforeBackup: _ =>
        {
            var archive = new GoalSnapshotArchive(SqliteOrchestratorStateRepository.HistorySerializerOptions);
            Execute(writer, "BEGIN IMMEDIATE");
            try
            {
                Assert.Equal(1, archive.ArchiveAsync(writer, LaterCutoff, cancellationToken: token).GetAwaiter().GetResult());
            }
            catch
            {
                Execute(writer, "ROLLBACK");
                throw;
            }
            Execute(writer, "COMMIT");
        });

        Assert.True(ReadValidStamp(fixture.Database, token).Generation > before.Generation);
        Assert.Contains(ReadEvidenceRows(fixture.Database), row => row.StartsWith(goalId, StringComparison.Ordinal));
        Assert.Equal(before, ReadValidStamp(workspace.SqliteStatePath, token));
        Assert.Equal(rows, ReadEvidenceRows(workspace.SqliteStatePath));
        await AssertReplayAsync(workspace, baseline, AssertNoPayloads, token);
    }

    [Fact]
    public async Task StampCheck_WithoutPayloadTables_UsesOneKeyedStatement()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var token = TestContext.Current.CancellationToken;
        await PrepareArchiveAsync(fixture, compress: true, token);
        var path = Copy(fixture, token).SqliteStatePath;
        var before = ReadValidStamp(path, token);
        using (var writer = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.ReadWrite))
            Execute(writer, "DROP TABLE goal_snapshot_archive; DROP TABLE provider_hold_seed_evidence; DROP TABLE goals");

        using var readOnly = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead);
        var trace = new BoardFillSnapshotSqlTrace();
        trace.Attach(path, readOnly);
        Assert.Equal(before, ProviderHoldIndexStamp.TryRead(readOnly, token));
        Assert.Equal(1, trace.Statements);
        Assert.Null(trace.Failure);

        using var explain = readOnly.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + ProviderHoldIndexStamp.ReadSql;
        using var plan = explain.ExecuteReader();
        var accesses = new List<string>();
        while (plan.Read())
            accesses.Add(plan.GetString(3));
        Assert.Collection(accesses,
            row => Assert.StartsWith("SEARCH provider_hold_index_stamp USING INTEGER PRIMARY KEY", row),
            row => Assert.StartsWith("SCAN pragma_user_version VIRTUAL TABLE", row));
    }

    private static async Task<(ProviderUsageHoldRow[] Status, ProviderUsageHoldRow[] Fleet, ProviderHoldSeedHistory History)>
        PrepareArchiveAsync(ProviderHoldSeedFixture fixture, bool compress, CancellationToken token)
    {
        await fixture.PopulateResetCaseAsync(legacyArchive: null, archiveRecovery: true);
        var reset = ProviderHoldSeedFixture.MakeUnrelated(805);
        var task = Assert.Single(reset.Tasks);
        var marker = ProviderBudgetHoldSchedules.FormatReset(new("anthropic::c:/profiles/held", ResetAt, ResetIntent));
        reset = reset with
        {
            Tasks = [task with { Status = WorkTaskStatus.Cancelled }],
            Timeline = [new(reset.Id, task.Id, ProgressKind.TaskNote, marker, ProviderHoldSeedFixture.At.AddDays(1))]
        };
        var unrelated = ProviderHoldSeedFixture.MakeUnrelated(1000);
        unrelated = unrelated with
        {
            Tasks = [Assert.Single(unrelated.Tasks) with { Status = WorkTaskStatus.Cancelled }],
            Timeline = [new(unrelated.Id, null, ProgressKind.TaskNote, "Unrelated outcome.", ProviderHoldSeedFixture.At.AddDays(1))]
        };
        await fixture.Repository.SaveGoalSnapshotsAsync([reset, unrelated], token);
        using (var writer = StateDbConnectionFactory.Open(fixture.Database, StateDbConnectionProfile.ReadWrite))
        using (var removeSuccess = writer.CreateCommand())
        {
            // Make canonical recovery depend on the archived goal, not the model-fit index.
            removeSuccess.CommandText = "DELETE FROM model_fit_history WHERE goal_id = $id";
            removeSuccess.Parameters.AddWithValue("$id", RecoveryGoal);
            removeSuccess.ExecuteNonQuery();
        }

        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
        var status = ReadStatus(workspace);
        var fleet = ProviderUsageHoldReader.ReadWorkspace(workspace, Now).ToArray();
        var row = Assert.Single(status);
        Assert.Equal(ProviderHoldSeedFixture.Id(799), row.Hold.SourceGoalId.Value);
        Assert.Equal(ResetIntent, row.Schedule?.ResetIntentId);
        Assert.Equal(ResetAt, row.Schedule?.NextProbeAt);
        Assert.Equal(status, fleet);
        var history = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database)
            .ReadProviderHoldStatusHistoryAsync(token);
        Assert.Contains(history.Recoveries, recovery => recovery.GoalId == RecoveryGoal);
        Assert.Equal(3, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Now,
            cancellationToken: token, compressSnapshots: compress));
        return (status, fleet, history);
    }

    private static OrchestratorWorkspace Copy(ProviderHoldSeedFixture fixture, CancellationToken token,
        Action<ShadowStartStore>? beforeBackup = null)
    {
        var directory = Directory.CreateDirectory(fixture.Scratch).FullName;
        var path = Path.Combine(directory, "diagnostic.db");
        CliShadowStartCommand.CopyStateDatabases(new(ShadowScratchRetention.Retain,
            [new(fixture.Database, path, fixture.Database)], []), new(BeforeBackup: beforeBackup),
            createOwnedStore: null, prepareStagedDatabase: null, token);
        return OrchestratorWorkspace.ForDirectory(directory) with { SqliteStatePath = path };
    }

    private static async Task<string> SaveNextArchiveAsync(ProviderHoldSeedFixture fixture, CancellationToken token)
    {
        var goal = ProviderHoldSeedFixture.MakeUnrelated(1001);
        await fixture.Repository.SaveGoalSnapshotsAsync(
            [goal with { Tasks = [Assert.Single(goal.Tasks) with { Status = WorkTaskStatus.Cancelled }] }], token);
        return goal.Id;
    }

    private static async Task AssertReplayAsync(OrchestratorWorkspace workspace,
        (ProviderUsageHoldRow[] Status, ProviderUsageHoldRow[] Fleet, ProviderHoldSeedHistory History) baseline,
        Action<List<GoalSnapshotReadObservation>> assertReads, CancellationToken token)
    {
        var statusReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(baseline.Status, ReadStatus(workspace, statusReads.Add));
        assertReads(statusReads);
        var fleetReads = new List<GoalSnapshotReadObservation>();
        Assert.Equal(baseline.Fleet, ProviderUsageHoldReader.ReadWorkspace(workspace, Now, fleetReads.Add).ToArray());
        assertReads(fleetReads);
        var history = await SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .ReadProviderHoldStatusHistoryAsync(token);
        Assert.Equal(baseline.History.Attempts.ToArray(), history.Attempts.ToArray());
        Assert.Equal(baseline.History.Recoveries.ToArray(), history.Recoveries.ToArray());
    }

    private static ProviderUsageHoldRow[] ReadStatus(OrchestratorWorkspace workspace,
        Action<GoalSnapshotReadObservation>? observe = null)
    {
        var snapshot = ConductorStatusSnapshotReader.Read(workspace, owner: null, bootTime: Now,
            now: Now, fleetSummaryExpected: false, readObserver: observe);
        Assert.Equal("stopped", snapshot.ConductorState);
        return Assert.IsAssignableFrom<IReadOnlyList<ProviderUsageHoldRow>>(snapshot.ProviderHolds).ToArray();
    }

    private static void AssertNoPayloads(List<GoalSnapshotReadObservation> reads)
    {
        foreach (var id in new[] { ResetGoal, RecoveryGoal })
            Assert.Contains(reads, read => read.GoalId == id && read.Operation == "provider-seed-archive-projection");
        Assert.DoesNotContain(reads, read => (read.GoalId == ResetGoal || read.GoalId == RecoveryGoal ||
            read.GoalId == UnrelatedGoal) && read.Operation is "provider-seed-discovery" or
            "provider-seed-snapshot-decode" or "archive-attempt" or "archive-deserialized");
    }

    private static ProviderHoldIndexStamp ReadValidStamp(string path, CancellationToken token)
    {
        using var connection = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead);
        var stamp = Assert.IsType<ProviderHoldIndexStamp>(ProviderHoldIndexStamp.TryRead(connection, token));
        Assert.True(stamp.IsValid);
        Assert.Equal(stamp.Generation, Scalar(connection, "PRAGMA user_version"));
        return stamp;
    }

    private static string[] ReadEvidenceRows(string path)
    {
        using var connection = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT goal_id || ':' || quote(marker) || ':' || format_version || ':' || hex(evidence_json)
            FROM provider_hold_seed_evidence ORDER BY goal_id
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
}
