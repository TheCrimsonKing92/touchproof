using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private temp stores, scoped owner binding and injected process identities.
public sealed class ActivationAdmissionSlotOwnerSourceTests
{
    [Fact]
    public void Read_OnlyLiveRunningUnreconciledOwnersWithPendingWork_Qualify()
    {
        using var fixture = new Fixture();
        var train = fixture.Attempt("train");
        var cohort = fixture.Attempt("cohort");
        var dead = train with { AttemptId = "dead" };
        var unknown = train with { AttemptId = "unknown" };
        var reserved = train with { AttemptId = "reserved" };
        var finished = train with { AttemptId = "finished", ResultPath = Path.Combine(fixture.Directory, "result"),
            ExitCodePath = Path.Combine(fixture.Directory, "exit") };
        File.WriteAllText(finished.ResultPath, "{\"Status\":\"completed\"}");
        File.WriteAllText(finished.ExitCodePath, "0");
        var owners = ActivationAdmissionSlotOwnerSource.Read(fixture.Directory,
            () => [train, train, cohort, dead, unknown, reserved, finished,
                train with { AttemptId = "reconciled", ReconciledAt = DateTimeOffset.UnixEpoch },
                train with { AttemptId = "failed", Outcome = "Failed" }, train with { Kind = "solo" }],
            attempt => attempt.AttemptId switch
            {
                "dead" => SpawnTrackedProcessStatus.DeadOrRecycled,
                "unknown" => SpawnTrackedProcessStatus.Unknown,
                _ => SpawnTrackedProcessStatus.LiveMatch
            },
            (_, attempt) => attempt.AttemptId == "reserved" ? ActivationAdmissionOwnerPendingStep.None :
                ActivationAdmissionOwnerPendingStep.Attribution);
        Assert.Equal(new[] { "cohort:cohort-owner", "train:train-owner" },
            owners.Select(owner => owner.Identity.Token).Order(StringComparer.Ordinal).ToArray());
        Assert.All(owners, owner => Assert.Equal(1234, owner.ProcessId));
        Assert.All(owners, owner => Assert.Contains("identity", owner.RunKeys));
    }

    [Fact]
    public void Read_EjectedTrainWithRealReceipt_PreparesUntilSurvivorsGate()
    {
        using var fixture = new Fixture();
        var store = new MergeTrainAcceptanceStore(Path.Combine(fixture.Directory, "merge-train-acceptance.db"));
        var identity = MergeTrainIdentity.Create([Binding('a'), Binding('b'), Binding('c')],
            new string('f', 40), new string('1', 40), "manifest");
        var receipt = new MergeTrainReceipt("red", identity, MergeTrainGateOutcome.Failed,
            DateTimeOffset.UnixEpoch, 1, ["tests"], 1, []) { GateAttemptId = "gate-attempt" };
        store.SaveGateReceipt(receipt);
        var attempt = fixture.Attempt("train") with { IdentityValue = identity.Value,
            Members = identity.Members.Select(member => new ConductorGroupedGateMember(member.GoalId.Value,
                member.BranchRevision, member.CandidateRevision, member.LandingPaths,
                member.ResourceKeys, member.ChangeRiskTier)).ToArray() };
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        Assert.Equal(ActivationAdmissionOwnerPendingStep.Attribution,
            ActivationAdmissionSlotOwnerSource.ReadPendingStep(fixture.Directory, attempt));
        store.RecordEjections("unrelated-gate", [new(new GoalId(new string('b', 32)),
            MergeTrainEjectionReason.RedNewestMember, [], "other gate")]);
        Assert.Equal(ActivationAdmissionOwnerPendingStep.Attribution,
            ActivationAdmissionSlotOwnerSource.ReadPendingStep(fixture.Directory, attempt));
        store.RecordEjections("gate-attempt", [new(new GoalId(new string('c', 32)),
            MergeTrainEjectionReason.RedNewestMember, [], "red member")]);

        var owners = ActivationAdmissionSlotOwnerSource.Read(fixture.Directory,
            classifyOwner: _ => SpawnTrackedProcessStatus.LiveMatch);
        Assert.Equal(ActivationAdmissionOwnerPendingStep.SurvivorsGate, Assert.Single(owners).PendingStep);
        var tally = ActivationAdmissionPreparingAttemptCensus.Evaluate([], [], DateTimeOffset.UnixEpoch,
            _ => SpawnTrackedProcessStatus.LiveMatch, owners);
        Assert.Equal(1, tally.Preparing);
        Assert.Equal("train:train-owner", tally.PreparingOwners);
        tally = ActivationAdmissionPreparingAttemptCensus.Evaluate([], [new(1234, null, 0, TimeSpan.Zero,
            RunClass: GateHeartbeatRunClass.Acceptance, RunId: "survivors")], DateTimeOffset.UnixEpoch,
            _ => SpawnTrackedProcessStatus.LiveMatch, owners);
        Assert.Equal(0, tally.Preparing);
        Assert.Equal(1, tally.Live);
    }

    [Theory]
    [InlineData("Passed", "PassedReceipt")]
    [InlineData("InfrastructureFailure", "None")]
    [InlineData("Invalidated", "None")]
    public void Read_TrainReceiptOutcome_RequiresQualifyingContinuation(string outcome, string expected)
    {
        using var fixture = new Fixture();
        var store = new MergeTrainAcceptanceStore(Path.Combine(fixture.Directory, "merge-train-acceptance.db"));
        var identity = MergeTrainIdentity.Create([Binding('a'), Binding('b')],
            new string('f', 40), new string('1', 40), "manifest");
        var trx = Path.Combine(fixture.Directory, "passed.trx");
        File.WriteAllText(trx, """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="survivors" outcome="Passed" /></Results>
              <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
            </TestRun>
            """);
        store.SaveGateReceipt(new("receipt", identity, Enum.Parse<MergeTrainGateOutcome>(outcome),
            DateTimeOffset.UnixEpoch, 1, [], 0, outcome == "Passed" ? [trx] : []));
        Assert.Equal(expected, ActivationAdmissionSlotOwnerSource.ReadPendingStep(fixture.Directory,
            fixture.Attempt("train") with { IdentityValue = identity.Value }).ToString());
    }

    [Theory]
    [InlineData("Passed", "NotApplicable", false, "PassedReceipt")]
    [InlineData("Failed", "NotApplicable", false, "Attribution")]
    [InlineData("Failed", "NotApplicable", true, "FocusedPartition")]
    [InlineData("Failed", "FirstMemberFailed", true, "None")]
    [InlineData("InfrastructureFailure", "NotApplicable", false, "None")]
    [InlineData("Invalidated", "NotApplicable", false, "None")]
    public void Read_CohortReceipt_ClassifiesPendingStepWithoutWriting(
        string outcome, string attribution, bool partition, string expected)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Directory, "cohort-acceptance.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE cohort_receipts(cohort_id TEXT, outcome TEXT, attribution TEXT);
                CREATE TABLE cohort_partition_receipts(cohort_id TEXT);
                INSERT INTO cohort_receipts VALUES ('identity', $outcome, $attribution);
                """;
            command.Parameters.AddWithValue("$outcome", outcome);
            command.Parameters.AddWithValue("$attribution", attribution);
            command.ExecuteNonQuery();
            if (partition)
            {
                command.CommandText = "INSERT INTO cohort_partition_receipts VALUES ('identity');";
                command.Parameters.Clear();
                command.ExecuteNonQuery();
            }
        }
        var before = File.ReadAllBytes(path);
        Assert.Equal(expected, ActivationAdmissionSlotOwnerSource.ReadPendingStep(
            fixture.Directory, fixture.Attempt("cohort")).ToString());
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("matching", false)]
    [InlineData("matching", true)]
    [InlineData("gone", false)]
    [InlineData("mismatched", false)]
    public void Read_TrainWithoutReceipt_TracksHolderAcrossSevenTicks(string holderState, bool databaseExists)
    {
        using var fixture = new Fixture();
        var liveHolder = new SpawnProcessIdentity(1234, DateTimeOffset.UnixEpoch, Path.Combine(fixture.Directory, "owner.exe"));
        var recordedHolder = holderState switch
        {
            "gone" => liveHolder with { ProcessId = 5678 },
            "mismatched" => liveHolder with { StartedAt = liveHolder.StartedAt.AddSeconds(-1) },
            _ => liveHolder
        };
        var attempt = fixture.SaveWithHolder("train", recordedHolder);
        if (databaseExists)
        {
            var store = new MergeTrainAcceptanceStore(Path.Combine(fixture.Directory, "merge-train-acceptance.db"));
            var unrelated = MergeTrainIdentity.Create([Binding('a'), Binding('b')],
                new string('f', 40), new string('1', 40), "manifest");
            store.SaveGateReceipt(new("unrelated", unrelated, MergeTrainGateOutcome.Invalidated,
                DateTimeOffset.UnixEpoch, 1, [], 0, []));
        }

        AssertSevenTicks(fixture, attempt, liveHolder, expectPreparing: holderState == "matching");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_CohortWithoutReceipt_TracksHolderAcrossSevenTicks(bool databaseExists)
    {
        using var fixture = new Fixture();
        var liveHolder = new SpawnProcessIdentity(1234, DateTimeOffset.UnixEpoch, Path.Combine(fixture.Directory, "owner.exe"));
        var attempt = fixture.SaveWithHolder("cohort", liveHolder);
        if (databaseExists)
            _ = new CohortAcceptanceStore(Path.Combine(fixture.Directory, "cohort-acceptance.db"));

        AssertSevenTicks(fixture, attempt, liveHolder, expectPreparing: true);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-schema")]
    [InlineData("unreadable")]
    public void Read_UnavailableRecordOrReceipt_ReturnsNoOwner(string fault)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Attempt("cohort");
        ConductorGroupedGateAttemptCoordinator.Save(attempt);
        if (fault == "malformed") File.WriteAllText(attempt.MetadataPath, "{");
        if (fault == "wrong-schema")
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(fixture.Directory, "cohort-acceptance.db"), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE other(value TEXT);";
            command.ExecuteNonQuery();
        }
        Assert.Empty(ActivationAdmissionSlotOwnerSource.Read(fixture.Directory,
            classifyOwner: _ => SpawnTrackedProcessStatus.LiveMatch,
            readPendingStep: fault == "unreadable" ? (_, _) => throw new IOException("receipt unavailable") : null));
        Assert.Empty(ActivationAdmissionSlotOwnerSource.Read(fixture.Directory,
            readAttempts: () => throw new UnauthorizedAccessException("records unavailable")));
    }

    [Fact]
    public void Read_OutsideActivationOrWithoutBoundDirectory_ReturnsEmpty()
    {
        using var owner = GateHeartbeatOwner.Push(null);
        using var active = ActivationAdmissionAccumulator.Begin(true);
        Assert.Empty(ActivationAdmissionSlotOwnerSource.Read());
        using var disabled = ActivationAdmissionAccumulator.Begin(false);
        Assert.Empty(ActivationAdmissionSlotOwnerSource.Read());
    }

    private static void AssertSevenTicks(Fixture fixture, ConductorGroupedGateAttempt attempt,
        SpawnProcessIdentity liveHolder, bool expectPreparing)
    {
        var elapsed = TimeSpan.Zero;
        var tracker = new ActivationAdmissionHealthTracker(() => elapsed);
        for (var tick = 1; tick <= 7; tick++)
        {
            elapsed = TimeSpan.FromSeconds((tick - 1) * 39);
            var now = DateTimeOffset.UnixEpoch + elapsed;
            var owners = ActivationAdmissionSlotOwnerSource.Read(fixture.Directory, classifyOwner: recorded =>
                recorded.OwnerProcessId != liveHolder.ProcessId ? SpawnTrackedProcessStatus.DeadOrRecycled :
                    SpawnProcessIdentityReader.EvaluateRecordedIdentity(new(recorded.OwnerProcessId,
                        recorded.OwnerProcessStartedAt!.Value, recorded.OwnerExecutablePath!), liveHolder, out _));
            if (expectPreparing)
                Assert.Equal(ActivationAdmissionOwnerPendingStep.AwaitingReceipt, Assert.Single(owners).PendingStep);
            else
                Assert.Empty(owners);

            var tally = ActivationAdmissionPreparingAttemptCensus.Evaluate([], [], now,
                _ => SpawnTrackedProcessStatus.LiveMatch, owners);
            Assert.Equal(0, tally.Live);
            Assert.Equal(expectPreparing ? 1 : 0, tally.Preparing);
            Assert.Equal(expectPreparing ? $"{attempt.Kind}:{attempt.AttemptId}" : "", tally.PreparingOwners);

            var verdict = tracker.Observe(new(tick, false, 5, 1, tally.Live,
                PreparingAttempts: tally.Preparing, PreparingOwners: tally.PreparingOwners), now);
            Assert.Equal(expectPreparing ? ActivationAdmissionHealthState.Healthy :
                tick < 7 ? ActivationAdmissionHealthState.StreakOpen : ActivationAdmissionHealthState.Tripped, verdict.State);
        }

        Assert.Equal(expectPreparing ? (ConductorActivationRevertReason?)null : ConductorActivationRevertReason.AdmissionStalled,
            tracker.Verdict.Reason);
    }

    private static MergeTrainMemberBinding Binding(char goal) => new(
        new GoalId(new string(goal, 32)), new string(goal, 40), new string(goal, 40),
        ["src/Example.cs"], [], ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto,
        "Clean", "NoConflictsDetected", new string(goal, 40));

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "activation-owners-" + Guid.NewGuid().ToString("N"));

        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);

        internal ConductorGroupedGateAttempt Attempt(string kind) => new(
            AttemptId: kind + "-owner", Kind: kind, Members: [], MainRevision: "main", CombinedTreeRevision: "tree",
            ManifestIdentity: "manifest", IdentityValue: "identity", StartedAt: DateTimeOffset.UnixEpoch,
            OwnerProcessId: 1234, LaunchingGenerationId: 12,
            MetadataPath: Path.Combine(Directory, "grouped-gate-attempts", kind + ".attempt.json"),
            ResultPath: Path.Combine(Directory, kind + ".result"), ExitCodePath: Path.Combine(Directory, kind + ".exit"),
            StdoutPath: "", StderrPath: "", ExecutionDirectory: Directory, PolicyJson: "{}",
            OwnerProcessStartedAt: DateTimeOffset.UnixEpoch, OwnerExecutablePath: "owner.exe");

        internal ConductorGroupedGateAttempt SaveWithHolder(string kind, SpawnProcessIdentity holder)
        {
            var attempt = Attempt(kind) with
            {
                OwnerProcessId = holder.ProcessId, OwnerProcessStartedAt = holder.StartedAt,
                OwnerExecutablePath = holder.ImagePath, HolderContractVersion = 1
            };
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            var binding = AcceptanceHolderBinding.For(attempt);
            var generation = holder with { ProcessId = 4321 };
            var lease = new AcceptanceHolderLeaseRecord(binding, binding.JobName, generation, holder, "Claimed",
                [new("claim", generation, holder.StartedAt, 0, "job-custody-ready")]);
            File.WriteAllText(binding.LeasePath, JsonSerializer.Serialize(lease));
            return attempt;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
