using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all artifact, ledger, and verdict files belong to a temporary directory.
public sealed class CostGoalOutcomeGateLedgerTests : IDisposable
{
    private const string Main = AcceptanceGateOutcomeLedgerPublicationTests.LaunchMain;
    private static readonly DateTimeOffset AdoptedAt = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EvaluatedAt = AdoptedAt.AddHours(3);
    private readonly string root = Directory.CreateTempSubdirectory("gate-outcome-ledger-").FullName;
    private CostGoalOutcomeStore Store => new(root);
    private string LedgerPath => Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);

    [Fact]
    public void Run_TailsOnlyOneNewLineAndMatchesScanVerdicts()
    {
        var goal = FileGoal(window: 3);
        var initial = Store.Read(goal)!;
        var prefixes = Enumerable.Range(1, 200).Select(index => WriteGate(index, index <= 2 ? Main : "other")).ToArray();
        var rows = Scan();
        Assert.Equal(200, rows.Length);
        SharedJsonlFile.AppendLines(LedgerPath, rows.Select(Serialize).Append("""{"kind":"backfill-complete"}"""));
        var expectedPending = ScanVerdicts(initial, rows);
        Assert.Equal(2, Assert.Single(expectedPending).AfterSampleSize);

        var sweep = CreateSweep();
        WithLockedGateFiles(prefixes, () => Run(sweep));
        Assert.Equal(0, sweep.GateLedger.GateFileReads);
        Assert.Equal(201, sweep.GateLedger.LinesReadLastRun);
        Assert.Equal(expectedPending, Store.Read(goal)!.PendingResults.ToArray());

        var appended = WriteGate(201, Main);
        var allRows = Scan();
        var expectedFinal = ScanVerdicts(initial, allRows);
        Assert.Equal(CostGoalOutcomeState.Met, Assert.Single(expectedFinal).State);
        Assert.Equal(3, expectedFinal[0].AfterSampleSize);
        SharedJsonlFile.AppendLine(LedgerPath, Serialize(allRows.Single(row => row.AttemptId == Path.GetFileName(appended))));

        WithLockedGateFiles(prefixes.Append(appended), () => Run(sweep));
        Assert.Equal(0, sweep.GateLedger.GateFileReads);
        Assert.Equal(1, sweep.GateLedger.LinesReadLastRun);
        Assert.Equal(expectedFinal, Store.Read(goal)!.FinalVerdicts.ToArray());
        Assert.Equal(allRows.Select(Project).ToArray(), sweep.GateLedger.Read(EvaluatedAt));
    }

    [Fact]
    public void Run_BackfillsTwentyGatesOnceAcrossSweepInstances()
    {
        var goal = FileGoal(window: 20);
        var prefixes = Enumerable.Range(1, 20).Select(index => WriteGate(index, Main)).ToArray();
        var expectedRows = Scan();
        Assert.Equal(20, expectedRows.Length);
        Assert.False(File.Exists(LedgerPath));

        var first = CreateSweep();
        Run(first);
        Assert.Equal(20, first.GateLedger.GateFileReads);
        var lines = File.ReadAllLines(LedgerPath);
        Assert.Equal(21, lines.Length);
        using var marker = JsonDocument.Parse(lines[^1]);
        Assert.Equal("backfill-complete", marker.RootElement.GetProperty("kind").GetString());
        Assert.Equal(expectedRows, lines[..^1].Select(line =>
            JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(line, AcceptanceGateOutcomeLedger.Json)).ToArray());
        var verdict = Assert.Single(Store.Read(goal)!.FinalVerdicts);
        Assert.Equal(20, verdict.AfterSampleSize);

        var freshGoal = FileGoal(window: 20);
        var second = CreateSweep();
        WithLockedGateFiles(prefixes, () => Run(second));
        Assert.Equal(0, second.GateLedger.GateFileReads);
        Assert.Equal(verdict, Assert.Single(Store.Read(freshGoal)!.FinalVerdicts));
        Assert.Equal(lines, File.ReadAllLines(LedgerPath));
    }

    [Theory]
    [InlineData(".result.json")]
    [InlineData(".lane-plan.json")]
    [InlineData(".attempt.json")]
    public void Run_RemovingOneAttemptArtifactMatchesScan(string suffix)
    {
        var goal = FileGoal(window: 3);
        var initial = Store.Read(goal)!;
        var (_, attempt) = AcceptanceGateOutcomeLedgerPublicationTests.PublishSolo(root, AdoptedAt.AddMinutes(1));
        WriteGate(1, Main);
        var sweep = CreateSweep();
        Run(sweep);
        Assert.Equal(2, Assert.Single(Store.Read(goal)!.PendingResults).AfterSampleSize);

        var removed = attempt.ResultPath[..^".result.json".Length] + suffix;
        Assert.True(File.Exists(removed));
        File.Delete(removed);
        Assert.True(Directory.Exists(Path.GetDirectoryName(removed)));
        Run(sweep);

        Assert.Equal(1, sweep.GateLedger.GateFileReads);
        Assert.Equal(0, sweep.GateLedger.LinesReadLastRun);
        Assert.Equal(ScanVerdicts(initial, Scan()), Store.Read(goal)!.PendingResults.ToArray());
        Assert.Equal(1, Assert.Single(Store.Read(goal)!.PendingResults).AfterSampleSize);
    }

    [Fact]
    public void Run_StaleSoloGateStopsCountingAndMatchesScan()
    {
        var goal = FileGoal(window: 2);
        var initial = Store.Read(goal)!;
        var (coordinator, attempt) = AcceptanceGateOutcomeLedgerPublicationTests.PublishSolo(root, AdoptedAt.AddMinutes(1));
        Assert.Single(Scan());
        SharedJsonlFile.AppendLine(LedgerPath, """{"kind":"backfill-complete"}""");
        var sweep = CreateSweep();
        Run(sweep);
        Assert.Equal(1, Assert.Single(Store.Read(goal)!.PendingResults).AfterSampleSize);

        Assert.True(coordinator.InvalidateCurrent(attempt.GoalId, "candidate changed"));
        var staleMetadata = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(attempt.MetadataPath), AcceptanceGateOutcomeLedger.Json)!;
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, staleMetadata.Outcome);
        var invalidation = Assert.Single(File.ReadLines(LedgerPath).Skip(2));
        using var row = JsonDocument.Parse(invalidation);
        Assert.Equal("invalidation", row.RootElement.GetProperty("kind").GetString());
        Assert.Equal(attempt.AttemptId, row.RootElement.GetProperty("attemptId").GetString());
        var scan = Scan();
        Assert.Empty(scan);

        Run(sweep);

        Assert.Equal(1, sweep.GateLedger.LinesReadLastRun);
        Assert.Equal(0, sweep.GateLedger.GateFileReads);
        Assert.Equal(ScanVerdicts(initial, scan), Store.Read(goal)!.PendingResults.ToArray());
        Assert.Equal(0, Assert.Single(Store.Read(goal)!.PendingResults).AfterSampleSize);
        Assert.Empty(new CostGoalOutcomeGateLedgerReader(root).Read(EvaluatedAt));
    }

    private GoalId FileGoal(int window)
    {
        var goal = GoalId.New();
        Store.RecordFiling(goal, new(AdoptedAt.AddDays(-1), null,
            [new(new("selection-milliseconds", "decrease", "20%", window), 100, window, null)]));
        return goal;
    }

    private CostGoalOutcomeSweep CreateSweep() => new(root, Store,
        () => (new BacklogStore(Path.Combine(root, "backlog.db")), new ConductEventLogWriter(Path.Combine(root, "conduct.log"))),
        _ => Main, () => [(Main, AdoptedAt)], (_, _) => false, failure => throw new InvalidOperationException(failure));

    private static void Run(CostGoalOutcomeSweep sweep) => sweep.Run(() => EvaluatedAt);

    private string WriteGate(int index, string commit)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, AcceptanceGateOutcomeLedger.AttemptRoot, "fixture"));
        var id = FormattableString.Invariant($"fixture-gate-{AdoptedAt.AddSeconds(index):yyyyMMddHHmmssfff}-{index:x32}");
        var prefix = Path.Combine(directory.FullName, id);
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, id, ["lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(10 + index % 12, index, null)
        };
        File.WriteAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix, plan.ToJson());
        var result = new AcceptanceVerificationResult(true, false, 0, null, TestResultPaths: [prefix + ".trx"]);
        AcceptanceAttemptResultRecord.Write(prefix + ".result.json", AcceptanceAttemptResultRecord.ForVerification(result, null, commit));
        return prefix;
    }

    private AcceptanceGateOutcomeObservation[] Scan() => CostGoalOutcomeMetric.ScanRecordedGateRows(root, EvaluatedAt).ToArray();

    private CostGoalOutcomeResult[] ScanVerdicts(CostGoalOutcomeRecord filing, IEnumerable<AcceptanceGateOutcomeObservation> rows) =>
        CostGoalOutcomeVerdict.Evaluate(filing, rows.Select(Project), Main, (_, _) => false, AdoptedAt, EvaluatedAt).ToArray();

    private OutcomeGateObservation Project(AcceptanceGateOutcomeObservation row) =>
        new(Path.Combine(root, "acceptance-gate-attempts", row.Directory, row.AttemptId), row.RecordedAt,
            row.StartedAt, row.LaunchMainCommit, row.Mode, row.FullGateReason, row.ExecutedClassCount, row.SelectionMilliseconds);

    private static string Serialize(AcceptanceGateOutcomeObservation row) =>
        JsonSerializer.Serialize(row, AcceptanceGateOutcomeLedger.Json);

    private static void WithLockedGateFiles(IEnumerable<string> prefixes, Action action)
    {
        var locks = new List<FileStream>();
        try
        {
            foreach (var prefix in prefixes)
            {
                locks.Add(File.Open(prefix + ".result.json", FileMode.Open, FileAccess.Read, FileShare.None));
                locks.Add(File.Open(prefix + AcceptanceGateLanePlan.ArtifactSuffix, FileMode.Open, FileAccess.Read, FileShare.None));
            }
            action();
        }
        finally { foreach (var file in locks) file.Dispose(); }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
