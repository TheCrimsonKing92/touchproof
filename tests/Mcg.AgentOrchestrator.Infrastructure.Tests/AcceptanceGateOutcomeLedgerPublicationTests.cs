using System.Security.AccessControl;
using System.Security.Principal;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_CapturesLaneSelectionWhenProgressOutputFails(bool solo)
    {
        using var output = new FailingProgressOutput();
        var prefix = solo
            ? PublishSolo(root, StartedAt, progressOutput: output).Attempt.ResultPath[..^".result.json".Length]
            : await PublishOwnedTrain(progressOutput: output);

        Assert.True(output.FlushAttempted);
        Assert.Contains("phase=lane-plan", output.ToString());
        AssertPublication(prefix, StartedAt, StartedAt, LaunchMain, solo ? "Passed" : "Failed",
            solo ? ResultMain : LaunchMain, hasSoloMetadata: solo);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Publication_PreservesTerminalResultWhenRecoveryDirectoryIsBlocked(bool solo, bool lockSharedFallback)
    {
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        var pendingDirectory = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName);
        var fallback = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingFileName);
        var fallbackDirectory = Path.Combine(root, AcceptanceGateOutcomeLedger.FallbackDirectoryName);
        const string marker = """{"kind":"backfill-complete"}""";
        SharedJsonlFile.AppendLine(ledger, marker);
        File.WriteAllText(pendingDirectory, "Blocks recovery directory creation.");
        if (lockSharedFallback) File.WriteAllText(fallback, "");

        string prefix;
        using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (lockSharedFallback ? File.Open(fallback, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
        {
            if (solo)
            {
                var (coordinator, attempt) = PublishSolo(root, StartedAt);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed,
                    coordinator.TryReadLatest(attempt.GoalId)!.Outcome);
                prefix = attempt.ResultPath[..^".result.json".Length];
            }
            else
            {
                prefix = await PublishOwnedTrain();
            }

            Assert.True(File.Exists(prefix + ".result.json"));
            Assert.Equal(marker, Assert.Single(File.ReadAllLines(ledger)));
            if (lockSharedFallback)
            {
                Assert.Empty(File.ReadAllLines(fallback));
                Assert.Single(Directory.GetFiles(fallbackDirectory, "*.jsonl"));
            }
            else Assert.Single(File.ReadAllLines(fallback));
        }

        var evaluatedAt = StartedAt.AddMinutes(1);
        var expected = Assert.Single(CostGoalOutcomeMetric.ScanRecordedGateRows(root, evaluatedAt));
        Assert.Equal(solo ? "Passed" : "Failed", expected.Outcome);
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        Assert.Equal(prefix, Assert.Single(reader.Read(evaluatedAt)).Id);
        Assert.Equal(0, reader.GateFileReads);
        Assert.False(File.Exists(fallback));
        if (lockSharedFallback) Assert.Empty(Directory.GetFiles(fallbackDirectory, "*.jsonl"));
        var lines = File.ReadAllLines(ledger);
        Assert.Equal(expected, JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(Assert.Single(lines.Skip(1)),
            AcceptanceGateOutcomeLedger.Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_RecoversAttemptIntentWhenAllSharedDestinationsAreBlocked(bool solo)
    {
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        var sharedPending = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingFileName);
        const string marker = """{"kind":"backfill-complete"}""";
        SharedJsonlFile.AppendLine(ledger, marker);
        File.WriteAllText(sharedPending, "");
        File.WriteAllText(Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName), "Blocks directory creation.");
        File.WriteAllText(Path.Combine(root, AcceptanceGateOutcomeLedger.FallbackDirectoryName), "Blocks directory creation.");
        var evaluatedAt = StartedAt.AddMinutes(1);
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        Assert.Empty(reader.Read(evaluatedAt));
        File.WriteAllText(sharedPending, "");

        string prefix;
        string pendingPath;
        using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (File.Open(sharedPending, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (solo)
            {
                var (coordinator, attempt) = PublishSolo(root, StartedAt);
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed,
                    coordinator.TryReadLatest(attempt.GoalId)!.Outcome);
                prefix = attempt.ResultPath[..^".result.json".Length];
            }
            else
            {
                prefix = await PublishOwnedTrain();
            }

            Assert.True(File.Exists(prefix + ".result.json"));
            Assert.Equal(marker, Assert.Single(File.ReadAllLines(ledger)));
            Assert.Empty(File.ReadAllLines(sharedPending));
            pendingPath = prefix + AcceptanceGateOutcomeLedger.AttemptPendingSuffix;
            Assert.Single(File.ReadAllLines(pendingPath));
        }

        var expected = Assert.Single(CostGoalOutcomeMetric.ScanRecordedGateRows(root, evaluatedAt));
        Assert.Equal(solo ? "Passed" : "Failed", expected.Outcome);
        using (File.Open(prefix + ".result.json", FileMode.Open, FileAccess.Read, FileShare.None))
        using (File.Open(prefix + AcceptanceGateLanePlan.ArtifactSuffix, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var freshReader = new CostGoalOutcomeGateLedgerReader(root);
            var recovered = Assert.Single(freshReader.Read(evaluatedAt));
            Assert.Equal(prefix, recovered.Id);
            Assert.Equal(recovered, Assert.Single(reader.Read(evaluatedAt)));
            Assert.Equal(0, freshReader.GateFileReads);
            Assert.Equal(0, reader.GateFileReads);
        }
        Assert.False(File.Exists(pendingPath));
        var line = Assert.Single(File.ReadAllLines(ledger).Skip(1));
        Assert.Equal(expected, JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(line, AcceptanceGateOutcomeLedger.Json));
    }

    [Fact]
    public async Task Publication_ReplaysFallbackAndReportsUnreadablePrimaryUntilItCanRecoverBoth()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows directory ACLs.");
        if (!OperatingSystem.IsWindows()) return;
        var ledger = Path.Combine(root, AcceptanceGateOutcomeLedger.FileName);
        var pendingDirectory = new DirectoryInfo(Path.Combine(root, AcceptanceGateOutcomeLedger.PendingDirectoryName));
        var fallback = Path.Combine(root, AcceptanceGateOutcomeLedger.PendingFileName);
        SharedJsonlFile.AppendLine(ledger, """{"kind":"backfill-complete"}""");
        string primaryPrefix;
        using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
            primaryPrefix = await PublishOwnedTrain();
        var primaryIntent = Assert.Single(pendingDirectory.GetFiles("*.jsonl")).FullName;

        var access = pendingDirectory.GetAccessControl(AccessControlSections.Access);
        var original = access.GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        access.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory | FileSystemRights.CreateFiles,
            AccessControlType.Deny));
        var reader = new CostGoalOutcomeGateLedgerReader(root);
        var evaluatedAt = StartedAt.AddMinutes(1);
        string fallbackPrefix;
        try
        {
            pendingDirectory.SetAccessControl(access);
            Assert.Throws<UnauthorizedAccessException>(() => pendingDirectory.GetFiles("*.jsonl"));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(Path.Combine(pendingDirectory.FullName, "probe"), ""));
            using (File.Open(ledger, FileMode.Open, FileAccess.Read, FileShare.Read))
                fallbackPrefix = await PublishOwnedTrain();
            Assert.True(File.Exists(fallbackPrefix + ".result.json"));
            Assert.Single(File.ReadAllLines(fallback));

            Assert.Throws<UnauthorizedAccessException>(() => reader.Read(evaluatedAt));
            Assert.False(File.Exists(fallback));
            Assert.Equal(Path.GetFileName(fallbackPrefix), Assert.Single(ReadRows()).AttemptId);
        }
        finally
        {
            // SetAccessControl persists only sections marked as modified.
            access.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            pendingDirectory.SetAccessControl(access);
        }

        Assert.True(File.Exists(primaryIntent));
        Assert.Equal(new[] { primaryPrefix, fallbackPrefix }.Order(StringComparer.Ordinal),
            reader.Read(evaluatedAt).Select(row => row.Id).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(primaryIntent));
        Assert.Equal(CostGoalOutcomeMetric.ScanRecordedGateRows(root, evaluatedAt).OrderBy(row => row.AttemptId),
            ReadRows().OrderBy(row => row.AttemptId));
        Assert.Equal(0, reader.GateFileReads);

        AcceptanceGateOutcomeObservation[] ReadRows() => File.ReadLines(ledger).Skip(1)
            .Select(line => JsonSerializer.Deserialize<AcceptanceGateOutcomeObservation>(line, AcceptanceGateOutcomeLedger.Json)!)
            .ToArray();
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

    private async Task<string> PublishOwnedTrain(Action? resultWritten = null, TextWriter? progressOutput = null)
    {
        var group = GoalId.New().Value;
        var attemptId = FormattableString.Invariant($"{group[..8]}-gate-{StartedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var directory = Directory.CreateDirectory(Path.Combine(root, "acceptance-gate-attempts", group));
        var prefix = Path.Combine(directory.FullName, attemptId);
        await using var owner = CreateOwner(root, group, prefix, writesResultRecord: true);
        PublishLanePlan(owner, progressOutput);

        owner.TryWriteResultRecord(new AcceptanceVerificationResult(false, false, 1, null,
            TestResultPaths: [prefix + ".trx"]), resultWrittenForTests: resultWritten);
        return prefix;
    }

    internal static (ConductorParallelAcceptanceAttemptCoordinator Coordinator, ConductorParallelAcceptanceAttempt Attempt)
        PublishSolo(string root, DateTimeOffset startedAt,
            Action<ConductorParallelAcceptanceAttemptCoordinator, ConductorParallelAcceptanceAttempt,
                ConductorParallelAcceptanceCandidate>? resultWritten = null,
            bool useDurableHolder = true, TextWriter? progressOutput = null)
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
                    PublishLanePlan(owner, progressOutput);
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

    private static void PublishLanePlan(AcceptanceAttemptExecutionOwner owner, TextWriter? progressOutput)
    {
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, owner.RunId, ["lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(12.345678901, 17, "fixture full gate")
        };
        AcceptanceGateLanePlanPublisher.Publish(plan, owner.ResultsPrefix, new GoalId(owner.Identity.GoalId),
            progress => AcceptanceGateProgressReporter.Emit(progress, null, owner, progressOutput ?? TextWriter.Null),
            TimeProvider.System);
    }

    private sealed class FailingProgressOutput : StringWriter
    {
        internal bool FlushAttempted { get; private set; }

        public override void Flush()
        {
            FlushAttempted = true;
            throw new IOException("Progress output is unavailable.");
        }
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
