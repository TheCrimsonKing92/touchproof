using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its filing records, gates, backlog, and conduct log.
public sealed class CostGoalOutcomeSweepTests : IDisposable
{
    private static readonly DateTimeOffset AdoptedAt = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly OutcomeMetricLine Selection = new("selection-milliseconds", "decrease", "20%", 3);
    private static readonly OutcomeMetricLine Classes = new("executed-class-count", "decrease", "15", 4);
    private const string Landed = "landed-commit";
    private const string Adopted = "first-containing-commit";
    private const string Unrelated = "unrelated-commit";

    private readonly string root = Directory.CreateTempSubdirectory("cost-goal-sweep-").FullName;
    private readonly CostGoalOutcomeStore store;
    private readonly BacklogStore backlog;
    private readonly ConductEventLogWriter events;
    private readonly Dictionary<GoalId, string> landings = new();
    private readonly List<(string Commit, DateTimeOffset At)> adoptions = [(Adopted, AdoptedAt)];
    private readonly List<string> failures = new();
    private readonly CostGoalOutcomeSweep sweep;
    private int gateNumber;

    public CostGoalOutcomeSweepTests()
    {
        store = new(root);
        backlog = new(Path.Combine(root, "backlog.db"));
        events = new(Path.Combine(root, "conduct-events.log"));
        sweep = CreateSweep();
    }

    [Fact]
    public void Run_CountsOnlyContainingGatesStartedAfterFirstContainingAdoption()
    {
        var goal = RecordFiling();
        adoptions.Insert(0, (Landed, AdoptedAt.AddHours(2)));
        adoptions.Add((Unrelated, AdoptedAt.AddHours(-2)));
        WriteGate(AdoptedAt.AddMinutes(1), Adopted, 999, startedAt: AdoptedAt.AddMinutes(-1));
        WriteGate(AdoptedAt.AddMinutes(2), Unrelated, 999);
        WriteGate(AdoptedAt.AddMinutes(3), Adopted, 60);
        WriteGate(AdoptedAt.AddMinutes(4), Landed, 70);

        Run();

        var pending = store.Read(goal)!;
        Assert.Equal(new(Landed, Adopted, AdoptedAt), pending.Adoption);
        Assert.Empty(pending.FinalVerdicts);
        var waiting = Assert.Single(pending.PendingResults);
        Assert.Equal(CostGoalOutcomeState.Pending, waiting.State);
        Assert.Equal(2, waiting.AfterSampleSize);
        Assert.Equal(3, waiting.Line.Window);
        Assert.False(File.Exists(events.CurrentPath));

        // The first event can rotate out of the history before the next qualifying gate lands.
        adoptions.RemoveAll(adoption => adoption.Commit == Adopted);
        WriteGate(AdoptedAt.AddMinutes(5), Adopted, 80);
        Run();

        var final = store.Read(goal)!;
        Assert.Equal(pending.Adoption, final.Adoption);
        Assert.Empty(final.PendingResults);
        Assert.Equal(new(0, Selection, CostGoalOutcomeState.Met, 100, 70, 6, 3, null),
            Assert.Single(final.FinalVerdicts));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Run_RepeatedMissDeliversOneVerdictAndBacklogNote()
    {
        var item = await backlog.AddAsync("Reduce gate selection cost");
        var goal = RecordFiling(item.Id);
        WriteMissingGates();

        Run();
        Run();

        var saved = store.Read(goal)!;
        Assert.Equal(CostGoalOutcomeState.Missed, Assert.Single(saved.FinalVerdicts).State);
        Assert.True(saved.BacklogApplied);
        Assert.Equal(0, Assert.Single(saved.EmittedLines));
        var note = Assert.Single((await backlog.GetByExactIdAsync(item.Id))!.Notes);
        Assert.Contains("verdict=missed before=100 after=95 target=20%", note.Text);
        var verdict = Assert.Single(ReadVerdictEvents());
        Assert.Equal(goal.Value, verdict.GoalId);
        Assert.Contains("verdict=missed", verdict.Detail);
        Assert.Empty(failures);
    }

    [Fact]
    public void Run_ReadsLegacyRecordAndPersistsEachPendingLine()
    {
        var goal = RecordFiling(includeClasses: true);
        var record = store.Read(goal)!;
        File.WriteAllText(RecordPath(goal), JsonSerializer.Serialize(new
        {
            record.Filing, record.FinalVerdicts, record.EmittedLines, record.BacklogNote, record.BacklogApplied
        }, Json));
        var legacy = store.Read(goal)!;
        Assert.Empty(legacy.PendingResults);
        Assert.Null(legacy.Adoption);
        Assert.Equal(record.Filing.Baselines.ToArray(), legacy.Filing.Baselines.ToArray());

        WriteGate(AdoptedAt.AddMinutes(1), Adopted, 75);
        Run();
        Assert.All(store.Read(goal)!.PendingResults, row => Assert.Equal(1, row.AfterSampleSize));
        WriteGate(AdoptedAt.AddMinutes(2), Adopted, 70);
        Run();

        var pending = store.Read(goal)!;
        Assert.Empty(pending.FinalVerdicts);
        Assert.Collection(pending.PendingResults,
            row => Assert.Equal(new(0, Selection, CostGoalOutcomeState.Pending, 100, null, 6, 2, null), row),
            row => Assert.Equal(new(1, Classes, CostGoalOutcomeState.Pending, 20, null, 7, 2, null), row));
        Assert.Empty(failures);
    }

    [Fact]
    public void Run_IneligibleGoalsProduceNoWritesOrEvents()
    {
        var finalGoal = RecordFiling();
        store.Write(finalGoal, store.Read(finalGoal)! with
        {
            FinalVerdicts = [new(0, Selection, CostGoalOutcomeState.Met, 100, 75, 6, 3, null)]
        });
        var noFiling = GoalId.New();
        landings.Add(noFiling, Landed);
        var noAdoption = RecordFiling();
        landings[noAdoption] = Unrelated;
        var records = new[] { RecordPath(finalGoal), RecordPath(noAdoption) };
        foreach (var path in records) File.SetLastWriteTimeUtc(path, AdoptedAt.UtcDateTime);
        var contents = records.Select(File.ReadAllText).ToArray();
        var modified = records.Select(File.GetLastWriteTimeUtc).ToArray();

        Run();

        Assert.Equal(contents, records.Select(File.ReadAllText).ToArray());
        Assert.Equal(modified, records.Select(File.GetLastWriteTimeUtc).ToArray());
        Assert.False(File.Exists(RecordPath(noFiling)));
        Assert.False(File.Exists(events.CurrentPath));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Run_RetriesUndeliveredBacklogChangeWithoutReplacingFinal()
    {
        var itemId = Guid.NewGuid().ToString("N");
        var goal = RecordFiling(itemId);
        WriteMissingGates();

        Run();

        var before = store.Read(goal)!;
        Assert.Equal(CostGoalOutcomeState.Missed, Assert.Single(before.FinalVerdicts).State);
        Assert.False(before.BacklogApplied);
        Assert.Contains(goal.Value, Assert.Single(failures));
        await backlog.UpsertAsync(new(itemId, "Reduce gate selection cost", "Fixture source", BacklogItemStatus.Open,
            AdoptedAt, AdoptedAt, SourceGoalId: null));

        Run();
        Run();

        var after = store.Read(goal)!;
        Assert.Equal(Assert.Single(before.FinalVerdicts), Assert.Single(after.FinalVerdicts));
        Assert.True(after.BacklogApplied);
        Assert.Single((await backlog.GetByExactIdAsync(itemId))!.Notes);
        Assert.Single(ReadVerdictEvents());
        Assert.Single(failures);
    }

    [Fact]
    public void Run_DefaultSweepReadsLandedHomeCommitAndRotatedAdoption()
    {
        var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root);
        var workspace = OrchestratorWorkspace.ForDirectory(root, root, null, registry);
        var goal = GoalId.New();
        var outcomeStore = new CostGoalOutcomeStore(workspace.OrchestratorDirectory);
        outcomeStore.RecordFiling(goal, new(AdoptedAt.AddDays(-1), null, [new(Selection, 100, 6, null)]));
        new MultiRepositoryLandingRecordStore(workspace.OrchestratorDirectory).Write(new MultiRepositoryLandingRecord(
            goal, "fixture", new Dictionary<string, string> { ["default"] = Landed },
            new Dictionary<string, string>(), ["default"], AdoptedAt.AddHours(-1)));
        var log = new ConductEventLogWriter(workspace.ConductEventsLogPath, maxBytes: 1, utcNow: () => AdoptedAt);
        log.Append("loop-handoff", null, $"FLEET_BUILD_ADOPTED commit={Landed} adopted=home", AdoptedAt);
        log.Append("loop-handoff", null, $"FLEET_BUILD_ADOPTED commit={Landed} adopted=home", AdoptedAt.AddHours(1));

        CostGoalOutcomeSweep.CreateDefault(workspace, failures.Add).Run(() => AdoptedAt.AddHours(2));

        var saved = outcomeStore.Read(goal)!;
        Assert.Equal(new(Landed, Landed, AdoptedAt), saved.Adoption);
        Assert.Equal(CostGoalOutcomeState.Pending, Assert.Single(saved.PendingResults).State);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Run_DefaultSweepResolvesPromotionWithoutLandingIntent()
    {
        var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root);
        var workspace = OrchestratorWorkspace.ForDirectory(root, root, null, registry);
        var goal = GoalId.New();
        var promoted = new string('c', 40);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.RecordPromotionFactsAsync([new(goal.Value, new string('b', 40), new string('a', 40),
            promoted, AdoptedAt.AddHours(-1), GoalPromotionFact.SoloProvenance)]);
        var outcomes = new CostGoalOutcomeStore(workspace.OrchestratorDirectory);
        outcomes.RecordFiling(goal, new(AdoptedAt.AddDays(-1), null, [new(Selection, 100, 6, null)]));
        new ConductEventLogWriter(workspace.ConductEventsLogPath).Append(
            "loop-handoff", null, $"FLEET_BUILD_ADOPTED commit={promoted} adopted=home", AdoptedAt);

        CostGoalOutcomeSweep.CreateDefault(workspace, failures.Add).Run(() => AdoptedAt.AddHours(2));

        var saved = outcomes.Read(goal)!;
        Assert.Equal(new(promoted, promoted, AdoptedAt), saved.Adoption);
        Assert.Equal(CostGoalOutcomeState.Pending, Assert.Single(saved.PendingResults).State);
        Assert.Empty(failures);
    }

    [Fact]
    public void Run_DefaultSweepResolvesTombstonedIntentAfterCompletedRecovery()
    {
        var registry = new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root);
        var workspace = OrchestratorWorkspace.ForDirectory(root, root, null, registry);
        var goal = GoalId.New();
        var unrelated = GoalId.New();
        var outcomes = new CostGoalOutcomeStore(workspace.OrchestratorDirectory);
        WriteJournal(goal, recoveryMinute: -3);
        WriteJournal(unrelated, recoveryMinute: -5);
        new ConductEventLogWriter(workspace.ConductEventsLogPath).Append(
            "loop-handoff", null, $"FLEET_BUILD_ADOPTED commit={Landed} adopted=home", AdoptedAt);

        CostGoalOutcomeSweep.CreateDefault(workspace, failures.Add).Run(() => AdoptedAt.AddHours(2));

        Assert.Equal(new(Landed, Landed, AdoptedAt), outcomes.Read(goal)!.Adoption);
        Assert.Null(outcomes.Read(unrelated)!.Adoption);
        Assert.Empty(failures);

        void WriteJournal(GoalId id, int recoveryMinute)
        {
            const string intent = GoalOperationJournal.LandingIntentOperation;
            var entries = new[]
            {
                Entry(intent, GoalOperationStatus.Completed, -4, Landed),
                Entry("conductor:landing-recovery", GoalOperationStatus.Completed, recoveryMinute),
                Entry("conductor:land", GoalOperationStatus.Completed, -2),
                Entry(intent, GoalOperationStatus.Failed, -1)
            };
            var path = GoalOperationJournal.PathFor(workspace.ExecutionDirectory, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = new JsonSerializerOptions(Json) { Converters = { new JsonStringEnumConverter() } };
            File.WriteAllLines(path, entries.OrderBy(entry => entry.At).Select(entry => JsonSerializer.Serialize(entry, json)));
            outcomes.RecordFiling(id, new(AdoptedAt.AddDays(-1), null, [new(Selection, 100, 6, null)]));

            GoalOperationJournalEntry Entry(string operation, GoalOperationStatus status, int minute, string? commit = null) =>
                new(GoalOperationJournal.Key(id, operation), id, operation, status, AdoptedAt.AddMinutes(minute), null, commit);
        }
    }

    private CostGoalOutcomeSweep CreateSweep() => new(root, store, () => (backlog, events), landings.GetValueOrDefault,
        () => adoptions, (ancestor, commit) => ancestor == Landed && commit == Adopted, failures.Add);

    private void Run() => sweep.Run(() => AdoptedAt.AddHours(3));

    private GoalId RecordFiling(string? backlogId = null, bool includeClasses = false)
    {
        var goal = GoalId.New();
        List<OutcomeMetricBaseline> baselines = [new(Selection, 100, 6, null)];
        if (includeClasses) baselines.Add(new(Classes, 20, 7, null));
        store.RecordFiling(goal, new(AdoptedAt.AddDays(-1), backlogId, baselines));
        landings.Add(goal, Landed);
        return goal;
    }

    private void WriteMissingGates()
    {
        WriteGate(AdoptedAt.AddMinutes(1), Adopted, 90);
        WriteGate(AdoptedAt.AddMinutes(2), Adopted, 95);
        WriteGate(AdoptedAt.AddMinutes(3), Adopted, 100);
    }

    private void WriteGate(DateTimeOffset recordedAt, string commit, double milliseconds, DateTimeOffset? startedAt = null)
    {
        var attempt = FormattableString.Invariant($"fixture-gate-{recordedAt:yyyyMMddHHmmssfff}-{++gateNumber:x32}");
        var directory = Directory.CreateDirectory(Path.Combine(root, "acceptance-gate-attempts", "fixture"));
        var prefix = Path.Combine(directory.FullName, attempt);
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, attempt, ["test-lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(milliseconds, 10, null)
        };
        File.WriteAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix, plan.ToJson());
        WriteGateResult(prefix, commit);
        if (startedAt is { } started)
        {
            var gateGoal = GoalId.New().Value;
            var metadata = new ConductorParallelAcceptanceAttempt(attempt, gateGoal, gateGoal[..8], 0, null, commit,
                started, recordedAt, 0, ConductorParallelAcceptanceAttemptOutcome.Passed,
                "", "", "", "", prefix + ".result.json", prefix + ".attempt.json", CompletedAt: recordedAt)
            {
                HolderCandidateKey = $"{gateGoal}:branch:{commit}"
            };
            File.WriteAllText(metadata.MetadataPath, JsonSerializer.Serialize(metadata, Json));
        }
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        if (gateNumber == 1)
            SharedJsonlFile.AppendLine(ledger, """{"kind":"backfill-complete"}""");
        var row = new AcceptanceGateOutcomeObservation(attempt, directory.Name, recordedAt, startedAt ?? recordedAt,
            commit, plan.SelectedExecution!.Mode, plan.SelectedExecution.FullGateReason,
            plan.SelectedExecution.ExecutedClassCount, milliseconds, "Passed")
        {
            HasSoloMetadata = startedAt.HasValue,
            OwnerFallbackLaunchMainCommit = startedAt.HasValue ? commit : null
        };
        SharedJsonlFile.AppendLine(ledger, JsonSerializer.Serialize(row, Json));
    }

    private static void WriteGateResult(string prefix, string commit)
    {
        var result = new AcceptanceVerificationResult(true, false, 0, null, TestResultPaths: [prefix + ".trx"]);
        AcceptanceAttemptResultRecord.Write(prefix + ".result.json", AcceptanceAttemptResultRecord.ForVerification(result, null, commit));
    }

    private string RecordPath(GoalId goal) => Path.Combine(root, "cost-goal-outcomes", $"{goal}.json");

    private IEnumerable<ConductEventRecord> ReadVerdictEvents() => File.ReadLines(events.CurrentPath)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!)
        .Where(row => row.EventKind == "cost-goal-outcome-verdict");

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
