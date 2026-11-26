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
        Assert.Matches(@"^PROVIDER_HOLD_HISTORY candidates=65 retained=0 goals= elapsedMs=\d+ loaded=0 historyMs=\d+ evidenceMs=\d+ pruneMs=\d+ historyCpuMs=\d+$",
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
    public async Task Seed_RecoveryWitnessSkipsFullLoadAndRestoresRunningProcessAcrossEvidenceVersions()
    {
        using var fixture = new Fixture();
        var first = Snapshot(0, false);
        var witness = Snapshot(1, true);
        var latest = Snapshot(2, false);
        var task = witness.Tasks[0];
        witness = witness with
        {
            Tasks = [task with
            {
                LastProcess = new(1234, "fixture", ".", "stdout", "stderr", "exit",
                    task.LastDispatch!.DispatchedAt, CompletedAt: null, ExitCode: null)
            }]
        };
        await fixture.Insert(first, witness, latest);
        fixture.UseRows(first, witness, latest);
        var witnessRow = $"SELECT snapshot_json FROM goals WHERE id = '{witness.Id}'";
        var canonicalBefore = fixture.Scalar(witnessRow);

        for (var round = 0; round < 3; round++)
        {
            if (round == 2)
            {
                var rows = await fixture.ReadProviderHoldSeedEvidenceAsync([new(witness.Id)]);
                var evidence = Assert.Single(rows!);
                fixture.ReplaceEvidence(witness.Id, 2, JsonSerializer.Serialize(evidence with
                {
                    Tasks = [Assert.Single(evidence.Tasks) with { LastProcess = null }]
                }, Options));
            }

            var kernel = fixture.Seed();
            Assert.Contains(latest.Id, fixture.Loaded);
            Assert.DoesNotContain(witness.Id, fixture.Loaded);
            Assert.Equal(round != 1, fixture.Decoded.Contains(witness.Id));
            var retained = Assert.Single(kernel.ProviderHoldHistory.Retained, goal => goal.Id.Value == witness.Id);
            Assert.True(Assert.Single(retained.Tasks).LastProcess is { ProcessId: 1234, IsRunning: true });
            Assert.Equal(latest.Id, Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, At.AddHours(1)))
                .Hold.SourceGoalId.Value);
            Assert.Equal("blob", fixture.Scalar($"SELECT typeof(evidence_json) FROM provider_hold_seed_evidence WHERE goal_id = '{witness.Id}'"));
            Assert.Equal(canonicalBefore, fixture.Scalar(witnessRow));
        }
    }

    [Fact]
    public async Task Read_BackfillAdvancesWithinByteLimitAndReturnsEveryProjection()
    {
        using var fixture = new Fixture();
        const int backfillByteLimit = 4 * 1024 * 1024;
        var snapshots = Enumerable.Range(0, 3).Select(index =>
        {
            var goal = Snapshot(index, false);
            var task = Assert.Single(goal.Tasks);
            // Spread the byte pressure across valid verification previews that survive replay.
            var tasks = Enumerable.Range(0, 72).Select(taskIndex =>
            {
                var output = new string((char)('a' + taskIndex % 26), 16 * 1024);
                var verification = task.LastVerification! with { StandardOutput = output };
                return task with
                {
                    Id = $"{goal.Id}-{taskIndex}",
                    LastVerification = verification,
                    VerificationHistory = [verification]
                };
            }).ToArray();
            return goal with { Tasks = tasks };
        }).ToArray();
        await fixture.Insert(snapshots);
        var ids = snapshots.Select(snapshot => new GoalId(snapshot.Id)).ToArray();
        var expected = snapshots.Select(snapshot => JsonSerializer.SerializeToUtf8Bytes(
            new ProviderUsageGoalEvidence(snapshot.Id, snapshot.Status, snapshot.Tasks.Select(task =>
                new ProviderUsageTaskEvidence(task.Id, task.RequiredRole, task.Status, task.AssignedAgentId,
                    task.LastDispatch, task.DispatchHistory!, task.LastVerification, task.VerificationHistory,
                    task.LatestProviderBudgetRecoveryAt)).ToArray(), []), Options)).ToArray();
        Assert.All(expected, json => Assert.InRange(json.Length, backfillByteLimit / 2 + 1, backfillByteLimit));

        for (var backfilled = 1; backfilled <= ids.Length; backfilled++)
        {
            fixture.Decoded.Clear();
            var evidence = await fixture.ReadProviderHoldSeedEvidenceAsync(ids);
            Assert.Equal(ids.Select(id => id.Value), evidence!.Select(row => row.GoalId));
            Assert.Equal(ids.Skip(backfilled - 1).Select(id => id.Value), fixture.Decoded);
            foreach (var (row, json) in evidence!.Zip(expected))
                Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(row, Options));
            Assert.Equal((long)backfilled, fixture.Scalar("SELECT count(*) FROM provider_hold_seed_evidence"));
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
    [InlineData("version")]
    [InlineData("frame")]
    [InlineData("goal-id")]
    public async Task Seed_UnusableStoredRow_ProjectsAndReplaces(string defect)
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        fixture.Seed();
        var (version, payload) = defect switch
        {
            "version" => (999, (byte[])fixture.Scalar("SELECT evidence_json FROM provider_hold_seed_evidence")),
            "frame" => (ProviderHoldSeedEvidenceStore.FormatVersion, "MGB1"u8.ToArray()),
            "goal-id" => (ProviderHoldSeedEvidenceStore.FormatVersion,
                GoalSnapshotArchiveCodec.Encode("""{"GoalId":"wrong","Tasks":[],"Timeline":[]}""")),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };
        fixture.ReplaceEvidence(snapshot.Id, version, payload);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        Assert.Single(fixture.Seed().ProviderHoldHistory.Retained);
        Assert.Empty(fixture.Decoded);
    }

    [Fact]
    public async Task Seed_LegacyProjection_ReplaysApiAndModelIdentityFromCanonicalHistory()
    {
        using var fixture = new Fixture();
        var snapshot = Snapshot(0, false);
        var task = snapshot.Tasks.Single();
        var dispatch = task.LastDispatch! with
        {
            ModelName = "claude-sonnet-4-6",
            ClaudeCredentialSourceDirectory = "C:/fixture",
            ClaudeApiHoldIdentity = "claude-api:c:/fixture"
        };
        var verification = task.LastVerification! with
        {
            StandardError = "",
            StandardOutput = "You've hit your sonnet limit",
            ProviderFailureKind = ProviderFailureKind.Unknown
        };
        snapshot = snapshot with
        {
            Tasks = [task with
            {
                LastDispatch = dispatch, DispatchHistory = [dispatch],
                LastVerification = verification, VerificationHistory = [verification]
            }]
        };
        await fixture.Insert(snapshot);
        fixture.UseRows(snapshot);
        fixture.Seed();
        var evidence = Assert.Single((await fixture.ReadProviderHoldSeedEvidenceAsync([new(snapshot.Id)]))!);
        var oldDispatch = dispatch with { ModelName = null, ClaudeApiHoldIdentity = null };
        fixture.ReplaceEvidence(snapshot.Id, 1, JsonSerializer.Serialize(evidence with
        {
            Tasks = [Assert.Single(evidence.Tasks) with { LastDispatch = oldDispatch, DispatchHistory = [oldDispatch] }]
        }, Options));

        var kernel = fixture.Seed();
        Assert.Equal(new[] { snapshot.Id }, fixture.Decoded);
        var hold = Assert.Single(ProviderUsageHoldView.Read(kernel.ProviderHoldScope, At)).Hold;
        Assert.Equal("xai::claude-api:c:/fixture|model=claude-sonnet-4-6", hold.BindingKey);
        Assert.Equal("claude-sonnet-4-6", hold.ModelName);
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

    [Fact]
    public async Task Seed_ColdDiscoveryAndProjection_SkipUnrelatedPayloadsAndReplayMixedHistory()
    {
        using var fixture = new Fixture();
        var source = Snapshot(0, false);
        var irrelevant = new string('x', 100_000);
        var witness = Snapshot(1, true) with { Objective = irrelevant };
        var taskId = witness.Tasks[0].Id;
        var reset = ProviderBudgetHoldSchedules.FormatReset(new("xai::<provider-default>", At, "intent"));
        source = source with { Timeline = [new(source.Id, null, ProgressKind.GoalPolicyDecision, irrelevant, At),
            new(source.Id, null, ProgressKind.TaskNote, reset, At, OperatorIntentCreatedAt: At)] };
        await fixture.Insert(source, witness);
        fixture.Execute($$"""
            INSERT INTO goal_timeline (goal_id, sequence, entry_json)
            SELECT goals.id, e.key, e.value FROM goals JOIN json_each(snapshot_json, '$.Timeline') e WHERE goals.id = '{{source.Id}}';
            UPDATE goals SET snapshot_json = json_remove(json_set(snapshot_json, '$.TimelineInTable', 2), '$.Timeline')
            WHERE id = '{{source.Id}}';
            INSERT INTO goal_task_history (goal_id, task_id, kind, sequence, entry_json)
            SELECT id, '{{taskId}}', 'verification', 0, json_extract(snapshot_json, '$.Tasks[0].LastVerification')
            FROM goals WHERE id = '{{witness.Id}}';
            UPDATE goals SET snapshot_json = json_remove(json_set(snapshot_json, '$.Tasks[0].HistoryInTable',
                json('{"Verification":1,"Dispatch":null,"LatestVerificationInTable":true}')),
                '$.Tasks[0].LastVerification', '$.Tasks[0].VerificationHistory')
            WHERE id = '{{witness.Id}}';
            """);
        var observations = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Path, observations.Add);
        var token = TestContext.Current.CancellationToken;
        var history = await repository.ReadProviderHoldSeedHistoryAsync(token);
        Assert.Equal(source.Id, Assert.Single(history.Attempts).GoalId);
        Assert.Equal(witness.Id, Assert.Single(history.Recoveries).GoalId);
        Assert.Equal(2, observations.Count(row => row.Operation == "provider-seed-discovery"));
        Assert.All(observations.Where(row => row.Operation == "provider-seed-discovery"),
            row => Assert.InRange(row.MaterializedCharacters, 1, irrelevant.Length - 1));
        var payloads = observations.Where(row => row.Operation == "provider-seed-timeline-entry").ToArray();
        Assert.Equal(2, payloads.Length);
        Assert.All(payloads, payload =>
        {
            Assert.Equal(source.Id, payload.GoalId);
            Assert.InRange(payload.MaterializedCharacters, 1, irrelevant.Length - 1);
        });
        var projected = Assert.Single(history.Evidence, row => row.GoalId == source.Id);
        Assert.Equal(reset, Assert.Single(projected.Timeline).Message);
        var canonical = await repository.LoadGoalsAsync([new(source.Id), new(witness.Id)], token);
        var kernel = new AgentOrchestratorKernel();
        Assert.NotNull(ProviderHoldHistoryStartupSeed.Apply(kernel, repository, _ => { }));
        Assert.Equal(ProviderUsageHoldView.Read(canonical.Goals, At.AddHours(1)).ToArray(),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, At.AddHours(1)).ToArray());

        fixture.Execute($"DELETE FROM goal_timeline WHERE goal_id = '{source.Id}' AND sequence = 0");
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            repository.ReadProviderHoldSeedEvidenceAsync([new(source.Id)], token));
        Assert.Contains("sequence gap", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/primary", true, true)]
    [InlineData("C:/alternate", true, false)]
    [InlineData("C:/primary", false, false)]
    public async Task Seed_CompressedArchives_ReuseEvidenceAndPreserveScopedRecovery(string credential,
        bool sameAttempt, bool clears)
    {
        using var fixture = new Fixture();
        var source = WithCredential(Snapshot(0, false), "C:/primary");
        var sourceTask = source.Tasks[0];
        var refusal = sourceTask.LastVerification! with
        {
            StandardError = "", AuthoritativeStandardError = "ERROR: weekly usage limit reached",
            ProviderFailureKind = ProviderFailureKind.Unknown, EvidenceSource = VerificationEvidenceSource.Dispatch,
            DispatchAttemptAt = sourceTask.LastDispatch!.DispatchedAt
        };
        source = source with { Tasks = [sourceTask with { LastVerification = refusal, VerificationHistory = [refusal] }] };
        var witness = WithCredential(Snapshot(1, true), credential);
        var witnessTask = witness.Tasks[0];
        var attempt = witnessTask.LastDispatch!.DispatchedAt.AddSeconds(sameAttempt ? 0 : -1);
        var receipt = new FindingEvidenceReceipt("fixture-receipt", "fixture-sha", new FindingEvidenceRequest([]),
            true, true, "fixture");
        var success = witnessTask.LastVerification! with
        {
            AuthoritativeStandardOutput = new string('x', 100_000),
            ModelFitNote = new string('y', 100_000),
            FindingEvidenceReceipts = [receipt],
            EvidenceSource = VerificationEvidenceSource.Dispatch,
            DispatchAttemptAt = attempt
        };
        witness = witness with { Tasks = [witnessTask with { LastVerification = success, VerificationHistory = [success] }] };
        await fixture.Insert(source, witness);
        var token = TestContext.Current.CancellationToken;
        var writer = new SqliteOrchestratorStateRepository(fixture.Path);
        await writer.EnsureTerminalOpenWorkIndexAsync(token);
        Assert.Equal(2, await writer.ArchiveTerminalGoalSnapshotsAsync(At.AddDays(2), compressSnapshots: true));
        Assert.Equal(2L, fixture.Scalar("SELECT count(*) FROM goal_snapshot_archive WHERE typeof(snapshot_json) = 'blob'"));

        var observations = new List<GoalSnapshotReadObservation>();
        var reader = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Path, observations.Add);
        var history = await reader.ReadProviderHoldSeedHistoryAsync(token);
        Assert.Equal(source.Id, Assert.Single(history.Attempts).GoalId);
        if (sameAttempt)
            Assert.Equal(witness.Id, Assert.Single(history.Recoveries).GoalId);
        else
            Assert.Empty(history.Recoveries);
        Assert.Equal(refusal.AuthoritativeStandardError,
            Assert.Single(history.Evidence.Single(row => row.GoalId == source.Id).Tasks).LastVerification!.AuthoritativeStandardError);
        if (sameAttempt)
        {
            var projected = Assert.Single(history.Evidence.Single(row => row.GoalId == witness.Id).Tasks).LastVerification!;
            Assert.Equal(attempt, projected.DispatchAttemptAt);
            Assert.True(projected.CompletionVerdictVerifiedSuccess);
            Assert.Equal(receipt.ReceiptId, Assert.Single(projected.FindingEvidenceReceipts!).ReceiptId);
            Assert.Null(projected.AuthoritativeStandardOutput);
            Assert.Null(projected.ModelFitNote);
        }
        else
            Assert.DoesNotContain(history.Evidence, row => row.GoalId == witness.Id);

        observations.Clear();
        var kernel = new AgentOrchestratorKernel();
        Assert.NotNull(ProviderHoldHistoryStartupSeed.Apply(kernel, reader, _ => { }));
        Assert.Equal(new[] { source.Id, witness.Id }, observations.Where(row => row.Operation == "provider-seed-archive-projection")
            .Select(row => row.GoalId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(observations, row => row.Operation == "provider-seed-snapshot-decode");

        var canonical = await reader.LoadGoalsAsync([new(source.Id), new(witness.Id)], token);
        var expected = ProviderUsageHoldView.Read(canonical.Goals, At.AddHours(1));
        Assert.Equal(clears ? 0 : 1, expected.Count);
        Assert.Equal(expected.ToArray(), ProviderUsageHoldView.Read(kernel.ProviderHoldScope, At.AddHours(1)).ToArray());

        static GoalSnapshot WithCredential(GoalSnapshot snapshot, string credential)
        {
            var task = snapshot.Tasks[0];
            var dispatch = task.LastDispatch! with { ClaudeCredentialSourceDirectory = credential };
            return snapshot with { Tasks = [task with { LastDispatch = dispatch, DispatchHistory = [dispatch] }] };
        }
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

        internal void ReplaceEvidence(string goalId, int format, object payload)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE provider_hold_seed_evidence SET format_version = $format, evidence_json = $payload
                WHERE goal_id = $id
                """;
            command.Parameters.AddWithValue("$format", format);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$id", goalId);
            Assert.Equal(1, command.ExecuteNonQuery());
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
