using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Text.Json;
using System.Text.Json.Serialization;

// Parallel-safe. Each case owns its SQLite store and kernel.
public sealed class ProviderHoldSeedWorkBoundTests
{
    [Fact]
    public async Task Seed_PersistedHistory_PreservesSchedulesWithinFixedWorkBound()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAsync();
        Assert.Equal(3200, fixture.CountDispatchGoals());
        var full = await fixture.Repository.ListModelFitHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3200, full.Select(row => row.GoalId).Distinct().Count());
        Assert.Equal(new[] { 200, 200, 200, 100, 100 }, ProviderHoldSeedFixture.Rules.Select(
            rule => full.Count(row => row.OutcomeRule == rule)));

        var kernel = new AgentOrchestratorKernel();
        var result = Seed(fixture, kernel);
        Assert.Equal(800, result.Selection.Sources.Count);
        Assert.Contains(ProviderHoldSeedFixture.Id(0), result.Selection.Sources);
        Assert.Equal(ProviderHoldSeedFixture.At, full.Single(row => row.GoalId == ProviderHoldSeedFixture.Id(0)).Timestamp);
        Assert.Equal(new[] { 800, 801, 802 }.Select(ProviderHoldSeedFixture.Id),
            result.Selection.Dependencies.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(ProviderHoldSeedFixture.Id(803), result.Selection.Dependencies);
        Assert.Equal(803, result.Index.IndexedGoals);
        Assert.Equal(8, result.Index.Evaluations.Values.Sum(entry => entry.Count));
        Assert.InRange(result.Index.Evaluations.Values.Sum(entry => entry.ObservationVisits), 1, 3212);

        var keys = new[]
        {
            "anthropic::c:/profiles/old", "anthropic::c:/profiles/auth",
            "anthropic::c:/profiles/models|model=claude-sonnet-4-6",
            "anthropic::c:/profiles/models|model=claude-opus-4-6",
            "anthropic::claude-api:c:/profiles/old", "anthropic::c:/profiles/cleared",
            "anthropic::c:/profiles/superseded", "anthropic::c:/profiles/held"
        };
        Assert.Equal(keys.Order(StringComparer.Ordinal),
            result.Index.Evaluations.Keys.Select(binding => binding.RenderedKey).Order(StringComparer.Ordinal));
        Assert.All(result.Index.Evaluations.Keys, binding => Assert.Equal("provider+credential", binding.Scope));
        var expectedSources = new[] { 0, 199, 299, 399, 499, -1, 699, 799 };
        for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
        {
            var evaluation = Assert.Single(result.Index.Evaluations, entry => entry.Key.RenderedKey == keys[keyIndex]).Value;
            Assert.Equal(1, evaluation.Count);
            var source = expectedSources[keyIndex];
            if (source < 0)
            {
                Assert.Null(evaluation.Schedule);
                continue;
            }

            var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule);
            Assert.Equal(ProviderHoldSeedFixture.Id(source), schedule.Hold.SourceGoalId.Value);
            Assert.Equal(ProviderHoldSeedFixture.Id(source), schedule.Hold.SourceTaskId.Value);
            Assert.Equal($"receipt-{source}", schedule.Hold.EvidenceReceipt);
            Assert.Equal(keyIndex == 1 ? ProviderFailureKind.AuthenticationRequired : ProviderFailureKind.BudgetExhausted,
                schedule.Reason);
            Assert.Equal(keyIndex == 6 ? 1 : 100, schedule.Attempt);
            Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(keyIndex == 0 ? 0 : source)
                .AddMinutes(keyIndex == 6 ? 30 : 360), schedule.NextProbeAt);
        }

        Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(0));
        Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(801));
        Assert.DoesNotContain(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(599));
        Assert.DoesNotContain(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(698));
        Assert.Equal(result.Index.Evaluations.Values.Where(entry => entry.Schedule is not null)
                .Select(entry => entry.Schedule!.Hold.SourceGoalId.Value).Order(StringComparer.Ordinal),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, ProviderHoldSeedFixture.At)
                .Select(row => row.Hold.SourceGoalId.Value).Order(StringComparer.Ordinal));
        var after = await fixture.Repository.ListModelFitHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(full.OrderBy(row => row.GoalId, StringComparer.Ordinal),
            after.OrderBy(row => row.GoalId, StringComparer.Ordinal));
        Assert.Equal(3200, fixture.CountDispatchGoals());
    }

    [Fact]
    public async Task Seed_MoreUnrelatedDispatches_LeavesIndexAndBindingWorkUnchanged()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAsync();
        var before = Seed(fixture, new());
        await fixture.AddUnrelatedAsync(3200);
        Assert.Equal(6400, fixture.CountDispatchGoals());
        var after = Seed(fixture, new());

        Assert.Equal(before.Selection.RowsRead, after.Selection.RowsRead);
        Assert.Equal(before.EvidenceGoals, after.EvidenceGoals);
        Assert.Equal(before.Index.IndexedGoals, after.Index.IndexedGoals);
        Assert.Equal(before.Index.DispatchMatchingVisits, after.Index.DispatchMatchingVisits);
        Assert.Equal(before.Index.RequiredGoals.Order(StringComparer.Ordinal), after.Index.RequiredGoals.Order(StringComparer.Ordinal));
        Assert.Equal(OrderedEvaluations(before), OrderedEvaluations(after));

        static IEnumerable<KeyValuePair<SubscriptionLimitEvidence.ProviderBindingKey, ProviderHoldSeedEvaluation>>
            OrderedEvaluations(ProviderHoldHistoryStartupSeed.Result result) =>
            result.Index.Evaluations.OrderBy(entry => entry.Key.Key, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.Scope, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.Model, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Seed_CrossGoalReset_KeepsMarkerWithoutPromotingItsGoalToSource(bool? legacyArchive, bool staleTimeline)
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateResetCaseAsync(legacyArchive, staleTimeline);
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
        var now = ProviderHoldSeedFixture.At.AddDays(2);
        var status = Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now));
        var fleet = Assert.Single(ProviderUsageHoldReader.ReadWorkspace(workspace, now));
        var unrelated = ProviderHoldSeedFixture.MakeUnrelated(806);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([unrelated], []));
        var seeded = Seed(fixture, kernel);
        Assert.Equal(new[] { ProviderHoldSeedFixture.Id(799) }, seeded.Selection.Sources);
        Assert.Empty(seeded.Selection.Dependencies);
        Assert.Equal(2, seeded.EvidenceGoals);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(
            Assert.Single(seeded.Index.Evaluations).Value.Schedule);
        Assert.Equal("fixture-reset", schedule.ResetIntentId);
        Assert.Equal(ProviderHoldSeedFixture.At.AddDays(2), schedule.NextProbeAt);
        Assert.Equal(new[] { 799, 805 }.Select(ProviderHoldSeedFixture.Id),
            kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id.Value));

        foreach (var row in new[] { status, fleet })
        {
            Assert.Equal(ProviderHoldSeedFixture.Id(799), row.Hold.SourceGoalId.Value);
            Assert.Equal(schedule.ResetIntentId, row.Schedule?.ResetIntentId);
            Assert.Equal(schedule.NextProbeAt, row.Schedule?.NextProbeAt);
        }

        Assert.Equal(1, kernel.EvictTerminalGoalAggregates([new GoalId(unrelated.Id)]));
        Assert.Equal(schedule, Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, now)).Schedule);
        Assert.Contains(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == ProviderHoldSeedFixture.Id(805));
    }

    [Fact]
    public async Task StatusAndFleet_ReplayUnclassifiedCompressedLegacyArchives()
    {
        using var fixture = new ProviderHoldSeedFixture();
        const int unrelatedArchives = 128;
        await fixture.PopulateResetCaseAsync(legacyArchive: true, unrelatedArchives: unrelatedArchives);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        var token = TestContext.Current.CancellationToken;
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('goal_snapshot_archive') WHERE name = 'provider_hold_empty_version'";
        Assert.Equal(0L, await command.ExecuteScalarAsync(token));

        var observations = new List<GoalSnapshotReadObservation>();
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database, observations.Add);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
        var now = ProviderHoldSeedFixture.At.AddDays(2);

        var status = Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now));
        var fleet = Assert.Single(ProviderUsageHoldReader.ReadWorkspace(workspace, now));

        foreach (var row in new[] { status, fleet })
        {
            Assert.Equal(ProviderHoldSeedFixture.Id(799), row.Hold.SourceGoalId.Value);
            Assert.Equal("fixture-reset", row.Schedule?.ResetIntentId);
            Assert.Equal(now, row.Schedule?.NextProbeAt);
        }
        var archivedIds = Enumerable.Range(1000, unrelatedArchives).Prepend(805).Select(ProviderHoldSeedFixture.Id);
        Assert.Equal(archivedIds.Order(StringComparer.Ordinal), observations
            .Where(item => item.Operation == "provider-seed-archive-projection")
            .Select(item => item.GoalId).Order(StringComparer.Ordinal));

        Assert.Equal(0L, await command.ExecuteScalarAsync(token));
    }

    [Fact]
    public async Task SeedAndStatus_SkipClassifiedLegacyArchivesAfterStartupBackfill()
    {
        using var fixture = new ProviderHoldSeedFixture();
        const int unrelatedArchives = 128;
        await fixture.PopulateResetCaseAsync(legacyArchive: true, unrelatedArchives: unrelatedArchives);
        var unrelatedIds = Enumerable.Range(1000, unrelatedArchives).Select(ProviderHoldSeedFixture.Id).ToHashSet();
        var observations = new List<GoalSnapshotReadObservation>();
        var writer = new SqliteOrchestratorStateRepository(fixture.Database, statementObserver: null,
            telemetryOptions: null, readObserver: observations.Add);
        var seeded = Seed(writer, new());
        Assert.Equal("fixture-reset", Assert.Single(seeded.Index.Evaluations).Value.Schedule?.ResetIntentId);
        Assert.Equal(unrelatedArchives, observations.Count(item => item.Operation == "provider-seed-archive-projection" &&
            item.GoalId is { } id && unrelatedIds.Contains(id)));

        observations.Clear();
        var restarted = new SqliteOrchestratorStateRepository(fixture.Database, statementObserver: null,
            telemetryOptions: null, readObserver: observations.Add);
        var replayed = Seed(restarted, new());
        Assert.Equal(seeded.Selection.Sources.Order(StringComparer.Ordinal),
            replayed.Selection.Sources.Order(StringComparer.Ordinal));
        Assert.Equal(Assert.Single(seeded.Index.Evaluations).Value.Schedule,
            Assert.Single(replayed.Index.Evaluations).Value.Schedule);
        Assert.Contains(observations, item => item.Operation == "provider-seed-archive-projection" &&
            item.GoalId == ProviderHoldSeedFixture.Id(805));
        Assert.DoesNotContain(observations, item => item.GoalId is { } id && unrelatedIds.Contains(id));

        observations.Clear();
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database, observations.Add);
        var row = Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], ProviderHoldSeedFixture.At.AddDays(2)));
        Assert.Equal("fixture-reset", row.Schedule?.ResetIntentId);
        Assert.Contains(observations, item => item.Operation == "provider-seed-archive-projection" &&
            item.GoalId == ProviderHoldSeedFixture.Id(805));
        Assert.DoesNotContain(observations, item => item.GoalId is { } id && unrelatedIds.Contains(id));

        using var smaller = new ProviderHoldSeedFixture();
        await smaller.PopulateResetCaseAsync(legacyArchive: true, unrelatedArchives: 8);
        Seed(smaller, new());
        var small = MeasureStatusDiscovery(smaller.Database);
        var large = MeasureStatusDiscovery(fixture.Database);
        Assert.Equal(2, large.Candidates);
        Assert.Equal(small, large);
        Assert.InRange(large.ScanSteps, 0, 2);

        // Both plans return the same candidates. The old join still walks every stub.
        var unindexed = MeasureStatusDiscovery(fixture.Database, indexed: false);
        Assert.Equal(large.Candidates, unindexed.Candidates);
        Assert.True(unindexed.ScanSteps >= unrelatedArchives);
        Assert.True(unindexed.VmSteps > large.VmSteps);
    }

    [Fact]
    public async Task StatusAndFleet_SeeNewResetAfterClassifiedArchiveWrites()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateResetCaseAsync(legacyArchive: true, unrelatedArchives: 8);
        Seed(fixture, new());
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
        var now = ProviderHoldSeedFixture.At.AddDays(2);
        Assert.Equal("fixture-reset", Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now))
            .Schedule?.ResetIntentId);

        // Re-saving a classified archive's goal makes the full hot document authoritative.
        var restored = WithReset(ProviderHoldSeedFixture.MakeUnrelated(1000), "fixture-reset-restored",
            now.AddHours(-2), now.AddDays(1));
        var token = TestContext.Current.CancellationToken;
        await fixture.Repository.SaveGoalSnapshotsAsync([restored], token);
        Assert.Equal("fixture-reset-restored", Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now))
            .Schedule?.ResetIntentId);

        var reset = WithReset(ClosedGoal(2000), "fixture-reset-archived", now.AddHours(-1), now.AddDays(2));
        var unrelated = ClosedGoal(2001);
        await fixture.Repository.SaveGoalSnapshotsAsync([reset, unrelated], token);
        Assert.Equal(2, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(now, cancellationToken: token,
            compressSnapshots: true));

        using var connection = new SqliteConnection($"Data Source={fixture.Database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(token);
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA data_version";
        var before = await version.ExecuteScalarAsync(token);
        var status = Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now));
        var fleet = Assert.Single(ProviderUsageHoldReader.ReadWorkspace(workspace, now));
        foreach (var row in new[] { status, fleet })
        {
            Assert.Equal(ProviderHoldSeedFixture.Id(799), row.Hold.SourceGoalId.Value);
            Assert.Equal("fixture-reset-archived", row.Schedule?.ResetIntentId);
            Assert.Equal(now.AddDays(2), row.Schedule?.NextProbeAt);
        }
        Assert.Equal(before, await version.ExecuteScalarAsync(token));
        Assert.Equal(4, MeasureStatusDiscovery(fixture.Database).Candidates);

        GoalSnapshot ClosedGoal(int index)
        {
            var goal = ProviderHoldSeedFixture.MakeUnrelated(index);
            return goal with
            {
                Tasks = [goal.Tasks.Single() with { Status = WorkTaskStatus.Cancelled }],
                Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, "Fixture outcome.", now.AddHours(-3))]
            };
        }

        static GoalSnapshot WithReset(GoalSnapshot goal, string id, DateTimeOffset recordedAt, DateTimeOffset retryAt)
        {
            var message = ProviderBudgetHoldSchedules.FormatReset(new("anthropic::c:/profiles/held", retryAt, id));
            return goal with
            {
                Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, message, recordedAt)]
            };
        }
    }

    private static (int Candidates, int ScanSteps, int VmSteps) MeasureStatusDiscovery(string database, bool indexed = true)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var sql = ProviderHoldSeedDiscovery.DocumentDiscoverySql(hasArchive: true, hasTimeline: true, hasHistory: true,
            hasArchiveDiscovery: !indexed, indexed: indexed, filterTextCandidates: true);
        using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using (var reader = explain.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.StartsWith(indexed
                ? "SCAN g USING INDEX " + GoalSnapshotArchive.ProviderHoldDiscoveryIndex
                : "SCAN g", reader.GetString(3));
        }

        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_prepare_v2(connection.Handle!, sql, out var statement));
        using (statement)
        {
            var candidates = 0;
            int result;
            while ((result = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
                candidates++;
            Assert.Equal(raw.SQLITE_DONE, result);
            return (candidates, raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_FULLSCAN_STEP, 0),
                raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 0));
        }
    }

    [Fact]
    public async Task Seed_CrossGoalReset_FailsWhenMarkerOwnerArchiveIsMissing()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateResetCaseAsync(null);
        var markerOwner = new GoalId(ProviderHoldSeedFixture.Id(805));
        using var connection = new SqliteConnection($"Data Source={fixture.Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", markerOwner.Value);
        command.CommandText = """
            INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at)
            SELECT id, snapshot_json, updated_at FROM goals WHERE id = $id;
            UPDATE goals SET snapshot_json = json_remove(
                json_set(snapshot_json, '$.IsMetadataOnly', json('true'), '$.Tasks', json('[]'), '$.Timeline', json('[]')),
                '$.TimelineInTable') WHERE id = $id;
            """;
        Assert.Equal(2, command.ExecuteNonQuery());
        var intact = Seed(fixture, new());
        Assert.Equal("fixture-reset", Assert.Single(intact.Index.Evaluations).Value.Schedule?.ResetIntentId);

        command.CommandText = "DELETE FROM goal_snapshot_archive WHERE goal_id = $id";
        Assert.Equal(1, command.ExecuteNonQuery());
        var history = await fixture.Repository.ReadProviderHoldSeedHistoryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(markerOwner, Assert.Single(history.MarkerGoals));

        var diagnostics = new List<string>();
        Assert.Null(ProviderHoldHistoryStartupSeed.Apply(new(), fixture.Repository, diagnostics.Add));
        Assert.Equal("PROVIDER_HOLD_HISTORY seed-failed: InvalidDataException: Required provider hold history is unavailable.",
            Assert.Single(diagnostics));
    }

    [Fact]
    public async Task Seed_PersistedProbesAndReplayedResets_HaveBoundedHistoryVisits()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateProbeCaseAsync();
        Assert.Equal(129, fixture.CountDispatchGoals());

        var seeded = Seed(fixture, new());
        var evaluation = Assert.Single(seeded.Index.Evaluations).Value;
        Assert.Equal(129, seeded.Index.IndexedGoals);
        Assert.Equal(1, evaluation.Count);
        Assert.InRange(evaluation.ObservationVisits, 129, 775);
        Assert.Equal(384, evaluation.ResetVisits);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule);
        Assert.Equal(ProviderHoldSeedFixture.Id(0), schedule.Hold.SourceGoalId.Value);
        Assert.Equal("fixture-probe-reset", schedule.ResetIntentId);
        Assert.Equal(129, schedule.Attempt);
        Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(744), schedule.NextProbeAt);
    }

    [Fact]
    public async Task Seed_ProbesOnOnePersistedTask_BoundDispatchVisits()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var probeGoal = new GoalId(ProviderHoldSeedFixture.Id(807));
        await fixture.PopulateSameTaskProbeCaseAsync();
        var canonical = await fixture.Repository.LoadGoalsAsync([probeGoal], TestContext.Current.CancellationToken);
        var persisted = Assert.Single(canonical.Goals);
        var task = Assert.Single(persisted.Tasks);
        Assert.Equal(128, task.DispatchHistory.Count);
        Assert.Equal(128, persisted.Timeline.Count(evt => ProviderBudgetHoldSchedules.TryParseProbe(evt, out _)));

        var evaluation = Assert.Single(Seed(fixture, new()).Index.Evaluations).Value;
        Assert.Equal(1, evaluation.Count);
        Assert.InRange(evaluation.DispatchVisits, 129, 1160);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule);
        Assert.Equal(2, schedule.Attempt);
        Assert.Equal("inconclusive", schedule.LastProbeOutcome);
        Assert.Equal(task.LastVerification!.CompletedAt, schedule.LastProbeAt);
        Assert.Equal(task.LastVerification.CompletedAt.AddMinutes(60), schedule.NextProbeAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_PersistedStandaloneResults_RespectTimelinePrecedenceWithBoundedResetVisits(bool backdated)
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateStandaloneResultCaseAsync(backdated);
        Assert.Equal(2, fixture.CountDispatchGoals());

        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(new SqliteOrchestratorStateRepository(fixture.Database), kernel);
        var evaluation = Assert.Single(seeded.Index.Evaluations).Value;
        Assert.Equal(2, seeded.Index.IndexedGoals);
        Assert.Equal(1, evaluation.Count);
        Assert.InRange(evaluation.ResetVisits, 390, 920);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule);
        Assert.Equal(ProviderHoldSeedFixture.Id(0), schedule.Hold.SourceGoalId.Value);
        Assert.Equal("fixture-hidden-reset-127", schedule.ResetIntentId);
        Assert.Equal(2, schedule.Attempt);
        Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(62), schedule.NextProbeAt);
        Assert.Equal("inconclusive", schedule.LastProbeOutcome);
        Assert.Equal(ProviderFailureKind.BudgetExhausted, schedule.LastActionableFailure);
        Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(2).AddSeconds(backdated ? -10 : 0), schedule.LastActionableFailureAt);
        Assert.Equal(schedule, ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, schedule.Hold,
            ProviderHoldSeedFixture.At.AddMinutes(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_AvailableCheckClearsOnlyAfterItsResetIsVisible(bool afterReset)
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateStandaloneResultCaseAsync(backdated: false, availableAfterReset: afterReset);
        var evaluation = Assert.Single(Seed(fixture, new()).Index.Evaluations).Value;
        if (afterReset) Assert.Null(evaluation.Schedule);
        else Assert.Equal(ProviderHoldSeedFixture.Id(0), Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Schedule)
            .Hold.SourceGoalId.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_RefusedAttemptSurvivesRequeueAndAlternateProviderSuccess(bool legacyModel)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var id = ProviderHoldSeedFixture.Id(0);
        var token = TestContext.Current.CancellationToken;
        await fixture.RequeueRefusedAttemptAsync();
        var repository = new SqliteOrchestratorStateRepository(fixture.Database);
        Assert.DoesNotContain(await repository.ListModelFitHistoryAsync(token), row => row.GoalId == id);
        AssertOriginalHold(Seed(repository, new()));

        await fixture.CompleteOnAlternateProviderAsync();
        repository = new(fixture.Database);
        var row = Assert.Single(await repository.ListModelFitHistoryAsync(token));
        Assert.Equal(id, row.GoalId);
        Assert.Equal("OpenAI", row.ProviderName);
        Assert.True(row.IsCompleted);
        AssertOriginalHold(Seed(repository, new()));
        var now = ProviderHoldSeedFixture.At.AddMinutes(4);
        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Database);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Source) with { SqliteStatePath = fixture.Database };
        var status = Assert.Single(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now));
        Assert.Equal(id, status.Hold.SourceGoalId.Value);
        Assert.Equal(status, Assert.Single(ProviderUsageHoldReader.ReadWorkspace(workspace, now)));

        await fixture.CompleteOnOriginalBindingAsync(legacyModel);
        var witness = ProviderHoldSeedFixture.Id(806);
        var rows = await repository.ListModelFitHistoryAsync(token);
        Assert.Equal(!legacyModel, rows.Any(row => row.GoalId == witness));
        var canonical = await repository.LoadGoalsAsync([new(id), new(witness)], token);
        Assert.Equal(2, canonical.Goals.Count);
        Assert.Empty(ProviderUsageHoldView.Read(canonical.Goals, now));
        var recovered = Seed(new SqliteOrchestratorStateRepository(fixture.Database), new());
        Assert.Equal(new[] { id }, recovered.Selection.Sources);
        Assert.Equal(new[] { witness }, recovered.Selection.Dependencies);
        Assert.Null(Assert.Single(recovered.Index.Evaluations).Value.Schedule);
        Assert.Empty(ProviderUsageHoldReader.ReadStatus(readOnly, [], [], now));
        Assert.Empty(ProviderUsageHoldReader.ReadWorkspace(workspace, now));

        void AssertOriginalHold(ProviderHoldHistoryStartupSeed.Result result)
        {
            Assert.Equal(new[] { id }, result.Selection.Sources);
            Assert.Empty(result.Selection.Dependencies);
            var evaluation = Assert.Single(result.Index.Evaluations);
            Assert.Equal("anthropic::c:/profiles/old", evaluation.Key.Key);
            var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Value.Schedule);
            Assert.Equal(id, schedule.Hold.SourceGoalId.Value);
            Assert.Equal(ProviderHoldSeedFixture.At, schedule.Hold.PlacedAt);
            Assert.Equal("receipt-0", schedule.Hold.EvidenceReceipt);
        }
    }

    [Fact]
    public async Task Seed_UnindexedRecoveryWithOrphanRefusal_MatchesCanonicalReplay()
    {
        using var fixture = new ProviderHoldSeedFixture();
        var source = ProviderHoldSeedFixture.Id(0);
        var witness = ProviderHoldSeedFixture.Id(806);
        await fixture.RequeueRefusedAttemptAsync();
        await fixture.CompleteOnOriginalBindingAsync(legacyModel: true, includeOrphanRefusal: true);
        var token = TestContext.Current.CancellationToken;
        var repository = fixture.Repository;
        Assert.DoesNotContain(await repository.ListModelFitHistoryAsync(token), row => row.GoalId == witness);
        var canonical = await repository.LoadGoalsAsync([new(source), new(witness)], token);
        Assert.Equal(2, canonical.Goals.Count);
        var recoveryGoal = Assert.Single(canonical.Goals, goal => goal.Id.Value == witness);
        Assert.Contains(recoveryGoal.Tasks, task => task.LastVerification is { } verification &&
            ProviderHoldEvidence.Read(verification).Confirmed);
        Assert.Empty(ProviderHoldHistory.SeedAttempts(recoveryGoal));
        Assert.Equal(witness, Assert.Single(ProviderHoldHistory.SeedRecoveryAttempts(recoveryGoal)).GoalId);
        var now = ProviderHoldSeedFixture.At.AddMinutes(4);
        var canonicalHolds = ProviderUsageHoldView.Read(canonical.Goals, now);
        Assert.Empty(canonicalHolds);

        var kernel = new AgentOrchestratorKernel();
        var result = Seed(repository, kernel);
        Assert.Equal(new[] { source }, result.Selection.Sources);
        Assert.Equal(new[] { witness }, result.Selection.Dependencies);
        Assert.Null(Assert.Single(result.Index.Evaluations).Value.Schedule);
        Assert.Equal(canonicalHolds.ToArray(), ProviderUsageHoldView.Read(kernel.ProviderHoldScope, now).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_LegacyRefusals_BoundMatchingAndExplicitRecoveryVisits(bool recovered)
    {
        using var fixture = new ProviderHoldSeedFixture();
        var id = ProviderHoldSeedFixture.Id(808);
        await fixture.PopulateLegacyRefusalCaseAsync(recovered);
        var token = TestContext.Current.CancellationToken;
        var canonical = await fixture.Repository.LoadGoalsAsync([new(id)], token);
        var task = Assert.Single(Assert.Single(canonical.Goals).Tasks);
        Assert.Equal(257, task.DispatchHistory.Count);
        Assert.Equal(20, task.VerificationHistory.Count);

        var discovery = await fixture.Repository.ReadProviderHoldSeedHistoryAsync(token);
        Assert.Equal(19, discovery.Attempts.Count);
        Assert.All(discovery.Attempts, attempt => Assert.Equal("Anthropic", attempt.ProviderName));
        Assert.Contains(discovery.Attempts, attempt => attempt.Timestamp == ProviderHoldSeedFixture.At.AddSeconds(45));
        // One build over 257 dispatches, 15 exact lookups and up to nine visits per binary lookup.
        // There are nine verification fallbacks and at most one explicit recovery lookup.
        Assert.InRange(discovery.DispatchMatchingVisits, 277, 370);

        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(fixture, kernel);
        Assert.Equal(new[] { id }, seeded.Selection.Sources);
        Assert.InRange(seeded.Index.DispatchMatchingVisits, 277, 370);
        var evaluation = Assert.Single(seeded.Index.Evaluations);
        Assert.Equal("anthropic::c:/profiles/old", evaluation.Key.Key);
        Assert.Equal(1, evaluation.Value.Count);
        Assert.Equal(19, evaluation.Value.DispatchVisits);
        if (recovered)
        {
            Assert.Null(evaluation.Value.Schedule);
            Assert.Empty(kernel.ProviderHoldHistory.Retained);
            return;
        }

        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(evaluation.Value.Schedule);
        Assert.Equal(id, schedule.Hold.SourceGoalId.Value);
        Assert.Equal("legacy-refusal-18", schedule.Hold.EvidenceReceipt);
        Assert.Equal(19, schedule.Attempt);
        Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(378).AddSeconds(1), schedule.NextProbeAt);
    }

    [Theory]
    [InlineData("overlapping", 2)]
    [InlineData("applicable", 1)]
    [InlineData("same-attempt", 1)]
    public async Task Seed_VerifiedRecovery_RespectsDispatchStartAndSameAttempt(string recovery, int expectedAttempts)
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateOverlappingAvailabilityAsync(recovery);
        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(fixture, kernel);
        var schedule = Assert.IsType<ProviderBudgetHoldSchedule>(Assert.Single(seeded.Index.Evaluations).Value.Schedule);
        Assert.Equal(ProviderHoldSeedFixture.Id(810), schedule.Hold.SourceGoalId.Value);
        Assert.Equal("receipt-810", schedule.Hold.EvidenceReceipt);
        Assert.Equal(expectedAttempts, schedule.Attempt);
        Assert.Equal(ProviderHoldSeedFixture.At.AddMinutes(expectedAttempts == 2 ? 64 : 34), schedule.NextProbeAt);
        Assert.Equal(recovery != "applicable", seeded.Index.RequiredGoals.Contains(ProviderHoldSeedFixture.Id(809)));
        Assert.Equal(schedule, Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope,
            ProviderHoldSeedFixture.At.AddMinutes(5))).Schedule);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seed_SharedBindingText_IsolatesCredentialScopes(bool retainProviderOnly)
    {
        const string literalCredential = "<provider-default>";
        var heldCredential = retainProviderOnly ? null : literalCredential;
        var recoveredCredential = retainProviderOnly ? literalCredential : null;
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateBindingScopeCollisionAsync(heldCredential, recoveredCredential);

        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(fixture, kernel);
        var source = ProviderHoldSeedFixture.Id(0);
        Assert.Equal(new[] { source, ProviderHoldSeedFixture.Id(1) }, seeded.Selection.Sources.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { ProviderHoldSeedFixture.Id(2) }, seeded.Selection.Dependencies);

        const string bindingText = "anthropic::<provider-default>";
        var heldScope = retainProviderOnly ? "provider-only" : "provider+credential";
        var recoveredScope = retainProviderOnly ? "provider+credential" : "provider-only";
        Assert.Equal(2, seeded.Index.Evaluations.Count);
        var held = seeded.Index.Evaluations[new(bindingText, heldScope)];
        var recovered = seeded.Index.Evaluations[new(bindingText, recoveredScope)];
        Assert.Equal(1, held.Count);
        Assert.Equal(1, recovered.Count);
        Assert.Null(recovered.Schedule);
        var hold = Assert.IsType<ProviderBudgetHoldSchedule>(held.Schedule).Hold;
        Assert.Equal(heldScope, hold.BindingScope);
        Assert.Equal(source, hold.SourceGoalId.Value);
        Assert.Equal("receipt-0", hold.EvidenceReceipt);
        Assert.Equal(new[] { source }, seeded.Index.RequiredGoals);
        Assert.Equal(source, Assert.Single(kernel.ProviderHoldHistory.Retained).Id.Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Seed_SharedBindingText_ReplaysOnlyTheResetTarget(bool modelCollision, bool explicitIdentity)
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateCollidingResetAsync(modelCollision, explicitIdentity);
        var kernel = new AgentOrchestratorKernel();
        var seeded = Seed(fixture, kernel);
        var first = new ProviderHoldBinding("Anthropic", modelCollision ? "/profiles/account|model=claude-sonnet-4-6" : null, null);
        var second = new ProviderHoldBinding("Anthropic", modelCollision ? "/profiles/account" : "<provider-default>",
            modelCollision ? "claude-sonnet-4-6" : null);
        var at = ProviderHoldSeedFixture.At;

        Assert.Equal(first.Key, second.Key);
        Assert.Equal(2, seeded.Index.Evaluations.Count);
        var firstEvaluation = seeded.Index.Evaluations[first.ScopedKey];
        var secondEvaluation = seeded.Index.Evaluations[second.ScopedKey];
        Assert.Equal(1, firstEvaluation.Count);
        Assert.Equal(1, secondEvaluation.Count);
        var unaffected = Assert.IsType<ProviderBudgetHoldSchedule>(firstEvaluation.Schedule);
        var reset = Assert.IsType<ProviderBudgetHoldSchedule>(secondEvaluation.Schedule);
        Assert.Equal(ProviderHoldSeedFixture.Id(0), unaffected.Hold.SourceGoalId.Value);
        Assert.Equal(ProviderHoldSeedFixture.Id(1), reset.Hold.SourceGoalId.Value);
        Assert.Equal(at.AddMinutes(30), unaffected.NextProbeAt);
        Assert.Null(unaffected.ResetIntentId);
        Assert.Equal(at.AddHours(3), reset.NextProbeAt);
        Assert.Equal("collision-reset", reset.ResetIntentId);
        Assert.Equal(new[] { unaffected, reset }, ProviderUsageHoldView.Read(kernel.ProviderHoldScope, at)
            .OrderBy(row => row.Hold.SourceGoalId.Value, StringComparer.Ordinal).Select(row => row.Schedule));
    }

    [Fact]
    public async Task Seed_AmbiguousCrossGoalReset_StaysAmbiguousWhenOneScopeIsCleared()
    {
        using var fixture = new ProviderHoldSeedFixture();
        await fixture.PopulateAmbiguousCrossGoalResetAsync();
        var unrelated = ProviderHoldSeedFixture.MakeUnrelated(4);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new([unrelated], []));
        var seeded = Seed(fixture, kernel);
        var at = ProviderHoldSeedFixture.At;
        var survivor = new ProviderHoldBinding("Anthropic", null, null);
        var cleared = new ProviderHoldBinding("Anthropic", "<provider-default>", null);
        Assert.Equal(survivor.Key, cleared.Key);
        Assert.Null(seeded.Index.Evaluations[cleared.ScopedKey].Schedule);
        var expected = Assert.IsType<ProviderBudgetHoldSchedule>(seeded.Index.Evaluations[survivor.ScopedKey].Schedule);
        Assert.Equal(at.AddMinutes(30), expected.NextProbeAt);
        Assert.Null(expected.ResetIntentId);

        var snapshot = await fixture.Repository.LoadGoalsAsync(Enumerable.Range(0, 4)
            .Select(index => new GoalId(ProviderHoldSeedFixture.Id(index))).ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(4, snapshot.Goals.Count);
        var canonical = ProviderUsageHoldView.Read(snapshot.Goals, at);
        Assert.Equal(2, canonical.Count);
        Assert.Contains(canonical, row => row.Schedule == expected);
        Assert.Equal(canonical, ProviderUsageHoldView.Read(kernel.ProviderHoldScope, at));

        Assert.Equal(1, kernel.EvictTerminalGoalAggregates([new GoalId(unrelated.Id)]));
        Assert.Equal(canonical, ProviderUsageHoldView.Read(kernel.ProviderHoldScope, at));
    }

    private static ProviderHoldHistoryStartupSeed.Result Seed(ProviderHoldSeedFixture fixture, AgentOrchestratorKernel kernel) =>
        Seed(fixture.Repository, kernel);

    private static ProviderHoldHistoryStartupSeed.Result Seed(SqliteOrchestratorStateRepository repository, AgentOrchestratorKernel kernel)
    {
        var diagnostics = new List<string>();
        var result = ProviderHoldHistoryStartupSeed.Apply(kernel, repository, diagnostics.Add);
        Assert.True(result is not null, string.Join(Environment.NewLine, diagnostics));
        return result!;
    }
}

internal sealed class ProviderHoldSeedFixture : IDisposable
{
    internal static readonly DateTimeOffset At = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static readonly string[] Rules =
    [
        "provider-budget-exhausted", "provider-authentication", "subscription-limit",
        "provider-rate-limit", "recoverable-subscription-limit"
    ];
    private static readonly string[] Credentials =
    [
        "old", "auth", "models", "models", "old", "cleared", "superseded", "held"
    ];
    private readonly string _root = Directory.CreateTempSubdirectory("provider-seed-").FullName;
    internal string Source { get; }
    internal string Scratch => Path.Combine(_root, "scratch");
    internal string Database { get; }
    internal SqliteOrchestratorStateRepository Repository { get; }

    internal ProviderHoldSeedFixture()
    {
        Source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Database = Path.Combine(Source, "state.db");
        StateDbMigrations.EnsureUpToDate(Database);
        Repository = new(Database);
    }

    internal static string Id(int index) => $"{index:x8}{new string('0', 24)}";

    internal Task PopulateAsync() => SaveAsync(Enumerable.Range(0, 3200).Select(index => index switch
    {
        < 800 => MakeHold(index),
        800 => Make(index, At.AddHours(20), Credentials[5], success: true),
        801 => Make(index, At.AddMinutes(698).AddSeconds(30), Credentials[6], success: true),
        802 => Make(index, At.AddHours(21), "ignored", success: true),
        803 => Make(index, At.AddSeconds(-1), Credentials[0], success: true),
        _ => MakeUnrelated(index)
    }));

    internal Task AddUnrelatedAsync(int count) => SaveAsync(Enumerable.Range(3200, count).Select(MakeUnrelated));

    internal Task PopulateBindingScopeCollisionAsync(string? heldCredential, string? recoveredCredential) => SaveAsync([
        Make(0, At, null, rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: "ERROR: weekly usage limit reached", apiIdentity: heldCredential),
        Make(1, At.AddMinutes(1), null, rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: "ERROR: weekly usage limit reached", apiIdentity: recoveredCredential),
        Make(2, At.AddMinutes(2), null, success: true, apiIdentity: recoveredCredential)
    ]);

    internal Task PopulateCollidingResetAsync(bool modelCollision, bool explicitIdentity)
    {
        const string model = "claude-sonnet-4-6";
        var first = Make(0, At, null, rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: "ERROR: weekly usage limit reached",
            apiIdentity: modelCollision ? "/profiles/account|model=claude-sonnet-4-6" : null);
        var credential = modelCollision ? "/profiles/account" : "<provider-default>";
        var second = Make(1, At.AddMinutes(1), null, rule: Rules[modelCollision ? 2 : 0],
            stdout: modelCollision ? "You've hit your sonnet limit" : "",
            stderr: modelCollision ? "" : "ERROR: weekly usage limit reached", model: model,
            apiIdentity: credential);
        var binding = new ProviderHoldBinding("Anthropic", credential, modelCollision ? model : null);
        var marker = ProviderBudgetHoldSchedules.FormatReset(new(binding.Key, At.AddHours(3), "collision-reset")
            { BindingIdentity = explicitIdentity ? binding.ScopedKey : null });
        return SaveAsync([first, second with
        {
            Timeline = [.. second.Timeline, new(second.Id, second.Tasks[0].Id, ProgressKind.TaskNote, marker, At.AddMinutes(2))]
        }]);
    }

    internal Task PopulateAmbiguousCrossGoalResetAsync()
    {
        const string refusal = "ERROR: weekly usage limit reached";
        const string clearedCredential = "<provider-default>";
        var survivor = Make(0, At, null, rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted, stderr: refusal);
        var cleared = Make(1, At.AddMinutes(1), null, rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: refusal, apiIdentity: clearedCredential);
        var recovery = Make(2, At.AddMinutes(2), null, success: true, apiIdentity: clearedCredential);
        var owner = Make(3, At.AddMinutes(3), "marker-owner", rule: Rules[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: refusal);
        var binding = new ProviderHoldBinding("Anthropic", null, null);
        var reset = ProviderBudgetHoldSchedules.FormatReset(new(binding.Key, At.AddHours(3), "ambiguous-reset"));
        return SaveAsync([survivor, cleared, recovery, owner with
        {
            Timeline = [.. owner.Timeline, new(owner.Id, owner.Tasks[0].Id, ProgressKind.TaskNote, reset, At.AddMinutes(4))]
        }]);
    }

    internal async Task PopulateResetCaseAsync(bool? legacyArchive, bool staleTimeline = false, int unrelatedArchives = 0)
    {
        var goal = MakeUnrelated(805);
        var marker = ProviderBudgetHoldSchedules.FormatReset(new(
            "anthropic::c:/profiles/held", At.AddDays(2), "fixture-reset"));
        goal = goal with { Timeline = [new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, marker, At.AddDays(1))] };
        var archives = Enumerable.Range(1000, unrelatedArchives).Select(MakeUnrelated).Prepend(goal).ToArray();
        await SaveAsync([MakeHold(799), .. archives]);
        if (legacyArchive is not { } compressed) return;

        // Write pre-projection archives with an authoritative inline timeline and no sidecars.
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO goal_snapshot_archive(goal_id, snapshot_json, archived_at) VALUES ($id, $archive, $at);
            DELETE FROM goal_timeline WHERE goal_id = $id;
            DELETE FROM goal_task_history WHERE goal_id = $id;
            UPDATE goals SET snapshot_json = $stub WHERE id = $id;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var archive = command.Parameters.Add("$archive", compressed ? SqliteType.Blob : SqliteType.Text);
        var stub = command.Parameters.Add("$stub", SqliteType.Text);
        command.Parameters.AddWithValue("$at", At.AddDays(2).ToString("O"));
        foreach (var archived in archives)
        {
            id.Value = archived.Id;
            var archivedJson = JsonSerializer.Serialize(archived, options);
            archive.Value = compressed ? GoalSnapshotArchiveCodec.Encode(archivedJson) : (object)archivedJson;
            stub.Value = JsonSerializer.Serialize(archived with { Tasks = [], Timeline = [], IsMetadataOnly = true }, options);
            Assert.True(command.ExecuteNonQuery() >= 2);
        }
        if (!staleTimeline) return;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", goal.Id);
        command.CommandText = """
            INSERT INTO goal_timeline(goal_id, sequence, entry_json)
            VALUES ($id, 0, json_object('Message', 'Ordinary stale sidecar row'))
            """;
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal Task PopulateProbeCaseAsync()
    {
        const string binding = "anthropic::c:/profiles/old";
        const string intent = "fixture-probe-reset";
        var firstReset = At.AddMinutes(1);
        var reset = ProviderBudgetHoldSchedules.FormatReset(new(binding, firstReset, intent));
        return SaveAsync(Enumerable.Range(0, 128).Select(index =>
        {
            var resetAt = firstReset.AddMinutes(3 * index);
            var probeAt = resetAt.AddMinutes(1);
            var goal = Make(1000 + index, resetAt.AddMinutes(2), Credentials[0], stderr: "fixture configuration failed");
            var probe = ProviderBudgetHoldSchedules.FormatProbe(new(binding, probeAt, index + 1, "operator")
            {
                HoldPlacedAt = At,
                ResetIntentId = intent
            });
            return goal with
            {
                Timeline =
                [
                    new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, reset, resetAt),
                    new(goal.Id, goal.Tasks[0].Id, ProgressKind.TaskNote, probe, probeAt)
                ]
            };
        }).Prepend(MakeHold(0)));
    }

    internal Task PopulateSameTaskProbeCaseAsync()
    {
        const string binding = "anthropic::c:/profiles/old";
        var lastStart = At.AddMinutes(128);
        var probeGoal = Make(807, lastStart.AddSeconds(1), Credentials[0], stderr: "fixture configuration failed");
        var task = probeGoal.Tasks[0];
        var dispatches = Enumerable.Range(1, 128).Select(attempt =>
            task.LastDispatch! with { DispatchedAt = At.AddMinutes(attempt) }).ToArray();
        var verification = task.LastVerification! with { DispatchStartedAt = lastStart, DispatchAttemptAt = lastStart };
        return SaveAsync([MakeHold(0), probeGoal with
        {
            Tasks = [task with
            {
                DispatchHistory = dispatches, LastDispatch = dispatches[^1],
                VerificationHistory = [verification], LastVerification = verification
            }],
            Timeline = dispatches.Select((dispatch, index) => new ProgressEventSnapshot(probeGoal.Id, task.Id,
                ProgressKind.TaskNote, ProviderBudgetHoldSchedules.FormatProbe(
                    new(binding, dispatch.DispatchedAt, index + 1, "backoff") { HoldPlacedAt = At }),
                dispatch.DispatchedAt)).ToArray()
        }]);
    }

    internal Task PopulateLegacyRefusalCaseAsync(bool recovered)
    {
        // Leave one slot under the 20-record restore cap for the unmatched exact attempt.
        const int refusalCount = 19;
        var lastRefusalStart = At.AddMinutes(refusalCount - 1);
        var goal = Make(808, At, Credentials[0], kind: ProviderFailureKind.BudgetExhausted,
            stderr: "ERROR: weekly usage limit reached");
        var task = goal.Tasks[0];
        var original = task.LastDispatch!;
        var alternate = original with { ProviderName = "OpenAI", ClaudeCredentialSourceDirectory = null };
        var dispatches = Enumerable.Range(0, 256).Select(index =>
            (index < refusalCount ? original : alternate) with { DispatchedAt = At.AddMinutes(index) }).ToList();
        dispatches.Insert(1, alternate with { DispatchedAt = At.AddSeconds(30) });
        var refusals = Enumerable.Range(0, refusalCount).Select(index => task.LastVerification! with
        {
            DispatchAttemptAt = null,
            DispatchStartedAt = (index % 4) switch
            {
                0 or 2 => At.AddMinutes(index),
                3 => At.AddMinutes(index).AddTicks(1),
                _ => null
            },
            CompletedAt = At.AddMinutes(index).AddSeconds(index == 0 ? 45 : 1),
            StandardErrorPath = $"legacy-refusal-{index}"
        }).ToList();
        refusals.Add(task.LastVerification! with
        {
            DispatchAttemptAt = At.AddTicks(-1),
            DispatchStartedAt = lastRefusalStart,
            CompletedAt = lastRefusalStart.AddSeconds(10)
        });
        return SaveAsync([goal with
        {
            Tasks = [task with
            {
                DispatchHistory = dispatches, LastDispatch = dispatches[^1],
                VerificationHistory = refusals, LastVerification = refusals[^1],
                LatestProviderBudgetRecoveryAt = recovered ? lastRefusalStart.AddSeconds(30) : null
            }]
        }]);
    }

    internal Task PopulateOverlappingAvailabilityAsync(string recovery)
    {
        var first = Make(809, At.AddMinutes(2), Credentials[0], rule: Rules[0],
            kind: ProviderFailureKind.BudgetExhausted, stderr: "ERROR: weekly usage limit reached");
        var last = Make(810, At.AddMinutes(4), Credentials[0], rule: Rules[0],
            kind: ProviderFailureKind.BudgetExhausted, stderr: "ERROR: weekly usage limit reached");
        var success = Make(811, At.AddMinutes(3), Credentials[0], success: true);
        var task = success.Tasks[0];
        var refusal = first.Tasks[0];
        var dispatch = recovery == "same-attempt" ? refusal.LastDispatch! : task.LastDispatch! with
        {
            DispatchedAt = recovery == "overlapping" ? At : At.AddMinutes(2)
        };
        var verification = task.LastVerification! with
        {
            DispatchAttemptAt = dispatch.DispatchedAt, DispatchStartedAt = dispatch.DispatchedAt
        };
        if (recovery == "same-attempt")
            return SaveAsync([first with
            {
                Tasks = [refusal with
                {
                    Status = WorkTaskStatus.Completed, LastVerification = verification,
                    VerificationHistory = [refusal.LastVerification!, verification]
                }]
            }, last]);
        return SaveAsync([first, last, success with
        {
            Tasks = [task with
            {
                LastDispatch = dispatch, DispatchHistory = [dispatch],
                LastVerification = verification, VerificationHistory = [verification]
            }]
        }]);
    }

    internal Task PopulateStandaloneResultCaseAsync(bool backdated, bool? availableAfterReset = null)
    {
        const string binding = "anthropic::c:/profiles/old";
        const string visibleIntent = "fixture-visible-reset";
        const string hiddenIntent = "fixture-hidden-reset-127";
        var checkedAt = At.AddMinutes(2);
        var hiddenAt = backdated ? checkedAt.AddSeconds(-10) : checkedAt;
        var source = MakeHold(0);
        var other = MakeUnrelated(805);
        var visible = ProviderBudgetHoldSchedules.FormatReset(new(binding, hiddenAt, visibleIntent));
        ProgressEventSnapshot Result(string intent, string outcome, string reason, DateTimeOffset at) =>
            new(source.Id, source.Tasks[0].Id, ProgressKind.TaskNote, ProviderBudgetHoldSchedules.FormatStandaloneResult(
                new(binding, At, at, 1, at, outcome, reason) { ResetIntentId = intent }), at);
        return SaveAsync([
            source with
            {
                Timeline =
                [
                    .. source.Timeline,
                    new(source.Id, source.Tasks[0].Id, ProgressKind.TaskNote, visible, checkedAt.AddMinutes(1),
                        OperatorIntentCreatedAt: At.AddMinutes(1)),
                    // Replay sorts by timestamp and preserves stored order at a tie. The
                    // authentication result would be applicable if the later reset were visible.
                    .. Enumerable.Range(0, 128).Select(index => Result(visibleIntent, "exhausted", $"refusal-{index}", hiddenAt)),
                    Result(hiddenIntent, availableAfterReset is null ? "authentication-required" : "available",
                        "generation is not yet visible", hiddenAt),
                    .. Enumerable.Range(0, 128).Select(index => new ProgressEventSnapshot(source.Id, source.Tasks[0].Id,
                        ProgressKind.TaskNote, ProviderBudgetHoldSchedules.FormatReset(
                            new(binding, hiddenAt, $"fixture-hidden-reset-{index}")), hiddenAt)),
                    Result(hiddenIntent, availableAfterReset == true ? "available" : "failed", "generation is now visible", checkedAt)
                ]
            },
            other with
            {
                Timeline = [new(other.Id, other.Tasks[0].Id, ProgressKind.TaskNote, visible, hiddenAt,
                    OperatorIntentCreatedAt: At.AddMinutes(1))]
            }
        ]);
    }

    internal async Task RequeueRefusedAttemptAsync()
    {
        var goal = MakeHold(0) with { Status = GoalStatus.Active };
        await SaveAsync([goal with
        {
            Tasks = [goal.Tasks[0] with
            {
                Status = WorkTaskStatus.Running, LastVerification = null, VerificationHistory = []
            }],
            Timeline = []
        }]);
        await SaveAsync([goal with
        {
            Tasks = [goal.Tasks[0] with { Status = WorkTaskStatus.Assigned }]
        }]);
    }

    internal Task CompleteOnAlternateProviderAsync()
    {
        var goal = MakeHold(0);
        var retry = Make(0, At.AddMinutes(2), Credentials[0], success: true);
        var refused = goal.Tasks[0];
        var completed = retry.Tasks[0];
        var alternate = completed.LastDispatch! with
        {
            ProviderName = "OpenAI", ModelName = "fixture-alternate", ClaudeCredentialSourceDirectory = null
        };
        return SaveAsync([goal with
        {
            Tasks = [completed with
            {
                VerificationHistory = [refused.LastVerification!, completed.LastVerification!],
                DispatchHistory = [refused.LastDispatch!, alternate],
                LastDispatch = alternate
            }],
            Timeline = [.. goal.Timeline, .. retry.Timeline]
        }]);
    }

    internal Task CompleteOnOriginalBindingAsync(bool legacyModel, bool includeOrphanRefusal = false)
    {
        var goal = Make(806, At.AddMinutes(3), Credentials[0], success: true);
        var task = goal.Tasks.Single();
        if (legacyModel)
        {
            var dispatch = task.LastDispatch! with { ModelName = null };
            task = task with { LastDispatch = dispatch, DispatchHistory = [dispatch] };
        }
        if (!includeOrphanRefusal) return SaveAsync([goal with { Tasks = [task] }]);

        var orphan = MakeHold(0).Tasks.Single() with
        {
            Id = Id(807), LastDispatch = null, DispatchHistory = []
        };
        return SaveAsync([goal with { Tasks = [task, orphan] }]);
    }

    internal Task PopulateRecoveredRefusalAsync()
    {
        var goal = MakeHold(0);
        var recovery = Make(0, At.AddMinutes(2), Credentials[0], success: true);
        var refused = goal.Tasks[0];
        var completed = recovery.Tasks[0];
        return SaveAsync([goal with
        {
            Tasks = [completed with
            {
                DispatchHistory = [refused.LastDispatch!, completed.LastDispatch!],
                VerificationHistory = [refused.LastVerification!, completed.LastVerification!]
            }]
        }]);
    }

    internal int CountDispatchGoals()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(DISTINCT goals.id) FROM goals
            JOIN goal_task_history dispatch ON dispatch.goal_id = goals.id AND dispatch.kind = 'dispatch'
            """;
        return checked((int)(long)command.ExecuteScalar()!);
    }

    private Task SaveAsync(IEnumerable<GoalSnapshot> snapshots) =>
        Repository.SaveGoalSnapshotsAsync(snapshots.ToArray(), TestContext.Current.CancellationToken);

    private static GoalSnapshot MakeHold(int index)
    {
        var key = index / 100;
        var rule = Rules[key % Rules.Length];
        var (stdout, stderr, model, kind) = key switch
        {
            1 => ("", "ERROR: 401 invalid credentials", "fixture-model", ProviderFailureKind.AuthenticationRequired),
            2 => ("You've hit your sonnet limit", "", "claude-sonnet-4-6", ProviderFailureKind.Unknown),
            3 => ("You've hit your opus limit", "", "claude-opus-4-6", ProviderFailureKind.Unknown),
            _ => ("", "ERROR: weekly usage limit reached", "fixture-model", ProviderFailureKind.BudgetExhausted)
        };
        return Make(index, At.AddMinutes(key == 0 ? 0 : index), Credentials[key], rule: rule, kind: kind,
            stdout: stdout, stderr: stderr, model: model, apiIdentity: key == 4 ? "claude-api:c:/profiles/old" : null);
    }

    internal static GoalSnapshot MakeUnrelated(int index) =>
        Make(index, At.AddDays(15).AddSeconds(index), Credentials[7], stderr: "fixture configuration failed");

    private static GoalSnapshot Make(int index, DateTimeOffset completed, string? credential, bool success = false,
        string? rule = null, ProviderFailureKind kind = ProviderFailureKind.Unknown, string stdout = "",
        string stderr = "", string model = "fixture-model", string? apiIdentity = null)
    {
        var id = Id(index);
        var started = completed.AddSeconds(-1);
        var dispatch = new TaskDispatchSnapshot("fixture", "fixture", ".", started, ProviderName: "Anthropic",
            ModelName: model, ClaudeCredentialSourceDirectory: credential is null ? null : $"C:/profiles/{credential}",
            ClaudeApiHoldIdentity: apiIdentity);
        var receipt = new TaskVerificationSnapshot("fixture", ".", success ? 0 : 1, stdout, stderr, completed,
            StandardErrorPath: $"receipt-{index}", DispatchStartedAt: started, DispatchAttemptAt: started,
            ProviderFailureKind: kind, CompletionVerdictVerifiedSuccess: success);
        var task = new TaskSnapshot(id, "Verify fixture", AgentRole.Tester,
            success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "tester",
            null, receipt, [receipt], dispatch, null, DispatchHistory: [dispatch], WorkerBuildInvocations: []);
        var note = rule is null ? "Fixture outcome." : $"CLASSIFIER rule={rule}; outcome_class=environmental;";
        return new(id, "Fixture history", GoalStatus.Completed, [task], [new(id, id, ProgressKind.TaskNote, note, completed)]);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
