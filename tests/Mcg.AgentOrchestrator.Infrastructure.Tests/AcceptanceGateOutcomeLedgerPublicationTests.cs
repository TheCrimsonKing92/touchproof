using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its attempt and ledger directories.
public sealed class AcceptanceGateOutcomeLedgerPublicationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("gate-outcome-publication-").FullName;
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 18, 1, 0, 0, TimeSpan.Zero);
    internal const string LaunchMain = "launch-main";
    private const string ResultMain = "result-main";

    [Fact]
    public void WrapperPublication_WritesOneRowFromTerminalMetadataAndLanePlan()
    {
        var (coordinator, attempt) = PublishSolo(root, StartedAt);
        var terminal = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(attempt.GoalId));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, terminal.Outcome);
        Assert.Equal(ResultMain, terminal.MainHeadSha);

        AssertPublication(attempt.ResultPath[..^".result.json".Length], StartedAt,
            terminal.StartedAt, LaunchMain, "Passed", ResultMain, hasSoloMetadata: true);
    }

    [Fact]
    public async Task OwnedTrainPublication_WritesOneRowFromResultAndLanePlan()
    {
        var prefix = await PublishOwnedTrain();
        AssertPublication(prefix, StartedAt, StartedAt, LaunchMain, "Failed", LaunchMain, hasSoloMetadata: false);
    }

    [Fact]
    public async Task OwnedTrainPublication_DoesNotDuplicateAnObservationAppendedByBackfill()
    {
        var prefix = await PublishOwnedTrain(() =>
        {
            var reader = new CostGoalOutcomeGateLedgerReader(root);
            Assert.Single(reader.Read(StartedAt.AddMinutes(1)));
            Assert.Equal(1, reader.GateFileReads);
        });

        var lines = File.ReadAllLines(Path.Combine(root, AcceptanceGateOutcomeLedger.FileName));
        var observations = lines.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(row => row.GetProperty("kind").ValueEquals(AcceptanceGateOutcomeLedger.GateKind));
        Assert.Equal(Path.GetFileName(prefix), Assert.Single(observations).GetProperty("attemptId").GetString());
        Assert.Equal(2, lines.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_RecoversOneObservationWhenLedgerAndLegacyPendingAreLocked(bool solo)
    {
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        var legacyPending = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingFileName);
        var pendingDirectory = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName);
        const string marker = """{"kind":"backfill-complete"}""";
        SharedJsonlFile.AppendLine(ledger, marker);
        var evaluatedAt = StartedAt.AddMinutes(1);
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        Assert.Empty(reader.Read(evaluatedAt));

        File.WriteAllText(legacyPending, "");
        string pending;
        using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (File.Open(legacyPending, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (solo)
                PublishSolo(root, StartedAt);
            else
                await PublishOwnedTrain();

            Assert.Equal(marker, Assert.Single(File.ReadAllLines(ledger)));
            Assert.Empty(File.ReadAllLines(legacyPending));
            pending = Assert.Single(Directory.GetFiles(pendingDirectory, "*.jsonl"));
            Assert.Single(File.ReadAllLines(pending));
            Assert.ThrowsAny<IOException>(() => reader.Read(evaluatedAt));
        }

        var expected = Assert.Single(CostGoalOutcomeMetric.ScanRecordedGateRows(root, evaluatedAt));
        var freshReader = new CostGoalOutcomeGateLedgerReader(root);
        var expectedGate = new OutcomeGateObservation(
            Path.Combine(root, AcceptanceGateOutcomeLedger.AttemptRoot, expected.Directory, expected.AttemptId),
            expected.RecordedAt, expected.StartedAt, expected.LaunchMainCommit, expected.Mode,
            expected.FullGateReason, expected.ExecutedClassCount, expected.SelectionMilliseconds);
        // Replay a legacy copy too, exercising deduplication across the two recovery paths.
        File.Copy(pending, legacyPending, overwrite: true);
        using (File.Open(pending, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Interrupt cleanup after replay so the next read must recognize the already published row.
            Assert.Equal(expectedGate, Assert.Single(freshReader.Read(evaluatedAt)));
            Assert.True(File.Exists(pending));
        }

        Assert.Equal(0, freshReader.GateFileReads);
        Assert.False(File.Exists(legacyPending));
        Assert.Equal(expectedGate, Assert.Single(reader.Read(evaluatedAt)));
        Assert.False(File.Exists(pending));
        Assert.Equal(1, reader.LinesReadLastRun);
        var lines = File.ReadAllLines(ledger);
        Assert.Equal(2, lines.Length);
        Assert.Equal(expected, JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(lines[1], AcceptanceGateOutcomeLedger.Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_RecoversAfterInterruptionAtResultVisibility(bool solo)
    {
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        SharedJsonlFile.AppendLine(ledger, """{"kind":"backfill-complete"}""");
        var pendingDirectory = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName);
        var interrupted = false;

        void Interrupt()
        {
            Assert.Single(Directory.GetFiles(pendingDirectory, "*.jsonl"));
            Assert.Single(File.ReadAllLines(ledger));
            interrupted = true;
            throw new IOException("Interrupted after result publication.");
        }

        if (solo)
        {
            PublishSolo(root, StartedAt, (coordinator, attempt, candidate) =>
            {
                // Legacy result adoption completes terminal metadata while the wrapper still owns the attempt.
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed,
                    coordinator.ObserveExistingAttempt(attempt, candidate).Attempt.Outcome);
                Interrupt();
            }, useDurableHolder: false);
        }
        else
        {
            await PublishOwnedTrain(Interrupt);
        }

        Assert.True(interrupted);
        Assert.Single(File.ReadAllLines(ledger));
        var evaluatedAt = StartedAt.AddMinutes(1);
        var expected = Assert.Single(CostGoalOutcomeMetric.ScanRecordedGateRows(root, evaluatedAt));
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        Assert.Equal(expected.AttemptId, Path.GetFileName(Assert.Single(reader.Read(evaluatedAt)).Id));
        Assert.Empty(Directory.GetFiles(pendingDirectory, "*.jsonl"));
        Assert.Single(reader.Read(evaluatedAt));
        var lines = File.ReadAllLines(ledger);
        Assert.Equal(2, lines.Length);
        Assert.Equal(expected, JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(lines[1], AcceptanceGateOutcomeLedger.Json));
    }

    [Fact]
    public async Task Publication_PreservesNextCompleteRowAfterInterruptedTail()
    {
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        SharedJsonlFile.AppendLine(ledger, """{"kind":"backfill-complete"}""");
        var evaluatedAt = StartedAt.AddMinutes(1);
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        Assert.Empty(reader.Read(evaluatedAt));
        File.AppendAllText(ledger, "{\"kind\":\"gate\"");
        Assert.Empty(reader.Read(evaluatedAt));

        var prefix = await PublishOwnedTrain();

        Assert.Equal(prefix, Assert.Single(reader.Read(evaluatedAt)).Id);
        Assert.Equal(prefix, Assert.Single(new CostGoalOutcomeGateLedgerReader(root).Read(evaluatedAt)).Id);
        var lines = File.ReadAllLines(ledger);
        Assert.Equal(3, lines.Length);
        Assert.Equal(Path.GetFileName(prefix), JsonSerializer
            .Deserialize<AcceptanceGateOutcomeObservation>(lines[2], AcceptanceGateOutcomeLedger.Json)!.AttemptId);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName), "*.jsonl"));
    }

    private async Task<string> PublishOwnedTrain(Action? resultWritten = null)
    {
        var group = GoalId.New().Value;
        var attemptId = FormattableString.Invariant($"{group[..8]}-gate-{StartedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(Path.Combine(root, "acceptance-gate-attempts", group));
        var prefix = Path.Combine(directory.FullName, attemptId);
        await using var owner = CreateOwner(root, group, prefix, writesResultRecord: true);
        PublishLanePlan(owner);

        owner.TryWriteResultRecord(new AcceptanceVerificationResult(false, false, 1, null,
            TestResultPaths: [prefix + ".trx"]), resultWrittenForTests: resultWritten);
        return prefix;
    }

    internal static (ConductorParallelAcceptanceAttemptCoordinator Coordinator, ConductorParallelAcceptanceAttempt Attempt)
        PublishSolo(string root, DateTimeOffset startedAt,
            Action<ConductorParallelAcceptanceAttemptCoordinator, ConductorParallelAcceptanceAttempt,
                ConductorParallelAcceptanceCandidate>? resultWritten = null,
            bool useDurableHolder = true)
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("Record a selected gate");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", LaunchMain);
        ConductorParallelAcceptanceAttemptCoordinator? coordinator = null;
        coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "acceptance-gate-attempts"), "main", utcNow: () => startedAt,
            runInline: true, acquireStableSlotLease: (_, _) => null,
            holderLease: useDurableHolder ? new AcceptanceHolderLease(utcNow: () => startedAt) : null,
            resultPublishedForTests: published => resultWritten?.Invoke(coordinator!, published, candidate));
        var attempt = coordinator.CreateAttemptForTests(candidate);
        coordinator.RunAttemptForTests(attempt, candidate, ConductorAutonomyPolicy.Permissive,
            (current, _, _, _, options) =>
            {
                var owner = CreateOwner(root, goal.Id.Value, options.ResultsPrefix!, options: options);
                return AcceptanceExecutionOwnerLifetime.Run(owner, () =>
                {
                    PublishLanePlan(owner);
                    return ConductorParallelAcceptanceRunResult.Accepted(current with { MainHeadSha = ResultMain },
                        new AcceptanceVerificationSummary(true, [], TestResultPaths: [options.ResultsPrefix + ".trx"]));
                });
            });
        return (coordinator, attempt);
    }

    private static AcceptanceAttemptExecutionOwner CreateOwner(string root, string goalId, string prefix,
        bool writesResultRecord = false, AcceptanceRunExecutionOptions? options = null) =>
        new(new(Path.GetFileName(prefix), goalId, root, "tree", LaunchMain, "branch", prefix, null, 0, null),
            new AcceptanceGateEngineSettings(), options: options, writesResultRecord: writesResultRecord);

    private static void PublishLanePlan(AcceptanceAttemptExecutionOwner owner)
    {
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, owner.RunId, ["lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(12.345678901, 17, "fixture full gate")
        };
        AcceptanceGateLanePlanPublisher.Publish(plan, owner.ResultsPrefix, new GoalId(owner.Identity.GoalId),
            owner.ReportProgress, TimeProvider.System);
    }

    private void AssertPublication(string prefix, DateTimeOffset recordedAt, DateTimeOffset startedAt,
        string launchMain, string outcome, string resultMain, bool hasSoloMetadata)
    {
        var plan = JsonSerializer.Deserialize<AcceptanceGateLanePlan>(
            File.ReadAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix), AcceptanceGateLanePlan.JsonOptions)!;
        var result = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
            File.ReadAllText(prefix + ".result.json"), AcceptanceGateOutcomeLedger.Json)!;
        Assert.Equal("accepted", result.Kind);
        Assert.Equal("gate", result.DispatchKind);
        Assert.Equal(resultMain, result.MainHeadSha);
        Assert.Equal(outcome == "Passed", result.Acceptance!.Passed);

        var line = Assert.Single(File.ReadAllLines(Path.Combine(root, AcceptanceGateOutcomeLedger.FileName)));
        using var json = JsonDocument.Parse(line);
        Assert.Equal("gate", json.RootElement.GetProperty("kind").GetString());
        var selection = plan.SelectedExecution!;
        Assert.Equal(new AcceptanceGateOutcomeObservation(plan.AttemptId, Path.GetFileName(Path.GetDirectoryName(prefix))!,
            recordedAt, startedAt, launchMain, selection.Mode, selection.FullGateReason,
            selection.ExecutedClassCount, selection.SelectionMilliseconds, outcome) { HasSoloMetadata = hasSoloMetadata },
            JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(line, AcceptanceGateOutcomeLedger.Json));
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
