using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each test owns its temporary database, kernels, connections and decode observer.
public sealed class ProviderHoldSeedStoredEvidenceTests
{
    [Fact]
    public async Task Seed_Current65Projections_DecodesNothingAndRetainsNothing()
    {
        using var fixture = new Fixture();
        var snapshots = Enumerable.Range(0, 65).Select(index => Snapshot(index, success: index >= 8)).ToArray();
        await fixture.Insert(snapshots);
        fixture.UseRows(snapshots);
        Assert.Equal(65, ProviderHoldHistory.SeedCandidates(fixture.Rows, new HashSet<string>()).Count);

        var cold = fixture.Seed();
        Assert.Equal(65, fixture.Decoded.Count);
        Assert.Empty(cold.ProviderHoldHistory.Retained);
        Assert.Equal(65L, fixture.Scalar("SELECT count(*) FROM provider_hold_seed_evidence"));
        Assert.Empty(fixture.Loaded);

        var warm = fixture.Seed();
        Assert.Empty(fixture.Decoded);
        Assert.Empty(fixture.Loaded);
        Assert.Empty(warm.ProviderHoldHistory.Retained);
        Assert.Matches(@"^PROVIDER_HOLD_HISTORY candidates=65 retained=0 goals= elapsedMs=\d+ loaded=0 historyMs=\d+ evidenceMs=\d+ pruneMs=\d+$",
            Assert.Single(fixture.Lines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_ArchivedActiveHold_MatchesFullLoadAcrossColdWarmAndMissing(bool compressed)
    {
        using var fixture = new Fixture();
        var snapshots = new[] { Snapshot(0, false), Snapshot(1, false) };
        await fixture.Insert(snapshots);
        fixture.UseRows(snapshots);
        var repository = new SqliteOrchestratorStateRepository(fixture.Path);
        await repository.EnsureTerminalOpenWorkIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM goal_open_work WHERE terminal_open_work = 1"));
        Assert.Equal(2, await repository
            .ArchiveTerminalGoalSnapshotsAsync(At.AddDays(2), compressSnapshots: compressed));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM goals WHERE json_extract(snapshot_json, '$.IsMetadataOnly') = 1"));
        Assert.Equal(2L, fixture.Scalar(compressed
            ? "SELECT count(*) FROM goal_snapshot_archive WHERE typeof(snapshot_json) = 'blob'"
            : "SELECT count(*) FROM goal_snapshot_archive WHERE typeof(snapshot_json) = 'text'"));
        var oracle = new ProviderHoldHistory();
        oracle.Retain(Kernel(snapshots).Goals, []);
        Assert.NotEmpty(oracle.Retained);
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(oracle.Scope([]), "xAI", null, out var expected));

        for (var round = 0; round < 3; round++)
        {
            if (round == 2) fixture.Execute("DROP TABLE provider_hold_seed_evidence");
            var actual = fixture.Seed();
            Assert.Equal(round == 1 ? 0 : 2, fixture.Decoded.Count);
            Assert.Equal(oracle.Retained.Select(goal => goal.Id), actual.ProviderHoldHistory.Retained.Select(goal => goal.Id));
            Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(actual.ProviderHoldScope, "xAI", null, out var hold));
            Assert.Equal(expected, hold);
            Assert.Equal(ProviderBudgetHoldSchedules.Evaluate(oracle.Scope([]), expected, expected.PlacedAt.AddHours(1)),
                ProviderBudgetHoldSchedules.Evaluate(actual.ProviderHoldScope, hold, expected.PlacedAt.AddHours(1)));
            Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM provider_hold_seed_evidence"));
        }
    }

    [Fact]
    public async Task Seed_MissingProjection_DecodesOnceAcrossRepositoryReconstruction()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Empty(fixture.Decoded);
        // Exercise the production repository seam as well as the counting wrapper.
        var evidence = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Path, fixture.Observe)
            .ReadProviderHoldSeedEvidenceAsync([new(snapshot.Id)]);
        Assert.Single(evidence!);
        Assert.Empty(fixture.Decoded);
    }

    [Fact]
    public async Task Seed_ChangedGoalRow_DecodesAgainAndUsesNewEvidence()
    {
        using var fixture = new Fixture();
        var original = Snapshot(0, false);
        await fixture.Insert(original);
        fixture.UseRows(original);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Single(fixture.Decoded);
        await fixture.Insert(Snapshot(0, true)); // Updates snapshot, version and updated_at together.
        Assert.Empty(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Equal(new[] { original.Id }, fixture.Decoded);
        Assert.Empty(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Empty(fixture.Decoded);
    }

    [Fact]
    public async Task Seed_ArchivedAfterBackfill_InvalidatesUnchangedGoalVersion()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        var version = fixture.Scalar("SELECT version FROM goals");
        var repository = new SqliteOrchestratorStateRepository(fixture.Path);
        await repository.EnsureTerminalOpenWorkIndexAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM goal_open_work WHERE terminal_open_work = 1"));
        Assert.Equal(1, await repository
            .ArchiveTerminalGoalSnapshotsAsync(At.AddDays(2), compressSnapshots: true));
        Assert.Equal(version, fixture.Scalar("SELECT version FROM goals"));
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Empty(fixture.Decoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_MissingOrMisshapenTable_PreservesOracle(bool misshapen)
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        fixture.Seed();
        fixture.Execute("DROP TABLE provider_hold_seed_evidence");
        if (misshapen) fixture.Execute("CREATE TABLE provider_hold_seed_evidence (wrong_column TEXT)");
        var oracle = new ProviderHoldHistory();
        oracle.Retain(Kernel(snapshot).Goals, []);
        var actual = fixture.Seed();
        Assert.NotEmpty(oracle.Retained);
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), actual.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        if (!misshapen) Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM provider_hold_seed_evidence"));
    }

    [Theory]
    [InlineData("UPDATE provider_hold_seed_evidence SET format_version = 999")]
    [InlineData("UPDATE provider_hold_seed_evidence SET evidence_json = 'invalid'")]
    [InlineData("UPDATE provider_hold_seed_evidence SET evidence_json = json_set(evidence_json, '$.GoalId', 'wrong')")]
    public async Task Seed_UnusableStoredRow_ProjectsAndReplaces(string mutation)
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        fixture.Seed();
        fixture.Execute(mutation);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Empty(fixture.Decoded);
    }

    [Fact]
    public async Task Read_SnapshotFailure_PersistsOnlyEarlierSuccessfulProjections()
    {
        using var fixture = new Fixture();
        var snapshots = new[] { Snapshot(0, false), Snapshot(1, false) };
        await fixture.Insert(snapshots);
        fixture.Execute("UPDATE goals SET snapshot_json = '{\"Id\":\"wrong\"}' WHERE id = '00000000000000000000000000000001'");
        var ids = snapshots.Select(snapshot => new GoalId(snapshot.Id)).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadProviderHoldSeedEvidenceAsync(ids));
        Assert.Equal(2, fixture.Decoded.Count);
        Assert.Equal(1L, fixture.Scalar("SELECT count(*) FROM provider_hold_seed_evidence"));
        fixture.Decoded.Clear();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadProviderHoldSeedEvidenceAsync(ids));
        Assert.Equal(new[] { snapshots[1].Id }, fixture.Decoded);
    }

    [Fact]
    public async Task Read_ReadOnlyMissingTable_ReturnsEvidenceWithoutWriting()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        var evidence = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Path, fixture.Observe)
            .ReadProviderHoldSeedEvidenceAsync([new(snapshot.Id)]);
        Assert.Equal(snapshot.Id, Assert.Single(evidence!).GoalId);
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        Assert.Equal(0L, fixture.Scalar("SELECT count(*) FROM sqlite_master WHERE name = 'provider_hold_seed_evidence'"));
    }

    [Fact]
    public async Task Read_ProjectionRoundTrip_PreservesEveryEvidenceField()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        var reset = ProviderBudgetHoldSchedules.FormatReset(new("xai::<provider-default>", At, "intent"));
        snapshot = snapshot with { Timeline = [.. snapshot.Timeline,
            new(snapshot.Id, snapshot.Tasks[0].Id, ProgressKind.TaskNote, reset, At.AddMinutes(3), OperatorIntentCreatedAt: At)] };
        await fixture.Insert(snapshot);
        var cold = await fixture.ReadProviderHoldSeedEvidenceAsync([new(snapshot.Id)]);
        Assert.Single(fixture.Decoded);
        fixture.Decoded.Clear();
        var warm = await fixture.ReadProviderHoldSeedEvidenceAsync([new(snapshot.Id)]);
        Assert.Empty(fixture.Decoded);
        Assert.Equal(JsonSerializer.Serialize(cold, Options), JsonSerializer.Serialize(warm, Options));
        Assert.Single(Assert.Single(warm!).Timeline);
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    private static GoalSnapshot Snapshot(int index, bool success)
    {
        var id = index.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        var at = At.AddMinutes(index * 10);
        var dispatch = new TaskDispatchSnapshot("fixture", "fixture", @"C:\fixture", at,
            ProviderName: "xAI", ModelName: "fixture");
        var verification = new TaskVerificationSnapshot("fixture", @"C:\fixture", success ? 0 : 1,
            "", success ? "" : "Provider budget exhausted.", at.AddMinutes(1),
            ProviderFailureKind: success ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
            DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: success);
        // Terminal tasks make these rows archive-eligible; provider policy still replays their receipts.
        // Match exported snapshots: JSON null build invocations are treated as unresolved archive work.
        var task = new TaskSnapshot(id, "Fixture task", AgentRole.Tester, WorkTaskStatus.Completed, "tester",
            null, verification, [verification], dispatch, null, DispatchHistory: [dispatch], WorkerBuildInvocations: []);
        return new(id, "Fixture goal", GoalStatus.Completed, [task],
            [new(id, null, ProgressKind.GoalPolicyDecision, "Fixture terminal", at.AddMinutes(2))]);
    }

    private static AgentOrchestratorKernel Kernel(params GoalSnapshot[] snapshots) =>
        AgentOrchestratorKernel.FromSnapshot(new(snapshots, []));

    private sealed class Fixture : IOrchestratorStateRepository, IDisposable
    {
        private readonly string _root = CreateTempDirectory();
        internal string Path { get; }
        internal List<string> Decoded { get; } = [];
        internal List<string> Loaded { get; } = [];
        internal List<string> Lines { get; } = [];
        internal IReadOnlyList<ModelFitHistoryRow> Rows { get; private set; } = [];

        internal Fixture()
        {
            Path = System.IO.Path.Combine(_root, "state.db");
            StateDbMigrations.EnsureUpToDate(Path);
        }

        internal async Task Insert(params GoalSnapshot[] snapshots)
        {
            using var connection = Open();
            foreach (var snapshot in snapshots)
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO goals (id, status, objective, snapshot_json, version, updated_at)
                    VALUES ($id, $status, $objective, $json, 1, $at)
                    ON CONFLICT(id) DO UPDATE SET snapshot_json = excluded.snapshot_json,
                        version = goals.version + 1, updated_at = excluded.updated_at
                    """;
                command.Parameters.AddWithValue("$id", snapshot.Id);
                command.Parameters.AddWithValue("$status", snapshot.Status.ToString());
                command.Parameters.AddWithValue("$objective", snapshot.Objective);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, Options));
                command.Parameters.AddWithValue("$at", At.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        internal void UseRows(params GoalSnapshot[] snapshots) => Rows = snapshots.Select(snapshot =>
        {
            var verification = snapshot.Tasks[0].LastVerification!;
            var success = verification.ExitCode == 0;
            return new ModelFitHistoryRow(snapshot.Id, snapshot.Tasks[0].Id, AgentRole.Tester, "xAI", "fixture",
                null, null, success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "adequate",
                verification.CompletedAt, success ? null : TaskOutcomeRules.ProviderBudgetExhausted.Token);
        }).ToArray();

        internal AgentOrchestratorKernel Seed()
        {
            Decoded.Clear();
            Loaded.Clear();
            Lines.Clear();
            var kernel = new AgentOrchestratorKernel();
            ProviderHoldHistoryStartupSeed.Apply(kernel, this, Lines.Add);
            Assert.DoesNotContain(Lines, line => line.Contains("seed-failed", StringComparison.Ordinal));
            return kernel;
        }

        internal void Observe(GoalSnapshotReadObservation observation)
        {
            if (observation.Operation == "provider-seed-snapshot-decode") Decoded.Add(observation.GoalId!);
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            return connection;
        }

        internal void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal object Scalar(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar()!;
        }

        public async Task<IReadOnlyList<ProviderUsageGoalEvidence>?> ReadProviderHoldSeedEvidenceAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            using var connection = Open();
            return await ProviderHoldSeedEvidenceStore.ReadAsync(connection, goalIds, Options, cancellationToken, Observe);
        }

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            Loaded.AddRange(goalIds.Select(id => id.Value));
            return new SqliteOrchestratorStateRepository(Path).LoadGoalsAsync(goalIds, cancellationToken);
        }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
