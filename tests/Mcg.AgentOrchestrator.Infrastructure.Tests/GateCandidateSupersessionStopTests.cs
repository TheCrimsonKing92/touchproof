using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique files per case, in-memory head observations and fake holder operations.
public sealed class GateCandidateSupersessionStopTests
{
    private const string Old = "1111111111111111111111111111111111111111";
    private const string New = "2222222222222222222222222222222222222222";

    [Fact]
    public void SoloAttempt_BranchHeadMoved_StopsThroughSoloCoordinatorAndReturnsGoalToVerified()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        Assert.True(f.Kernel.BeginGoalAcceptanceVerification(goal.Id, "gate"));
        var attempt = f.Single(goal);
        Assert.Single(f.Solo.GetAllCapacityReservingAttempts());
        f.Heads[goal.Id] = New;

        var result = f.Tick();

        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled, Fixture.ReadSolo(attempt).Outcome);
        Assert.Equal("Cancelled", f.Lease.Read(AcceptanceHolderBinding.For(attempt)).Phase);
        Assert.Single(f.CancelledJobs);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(new[] { goal.Id }, result.ChangedGoalIds);
        Assert.Null(goal.RetainedAcceptanceFailure);
        Assert.False(File.Exists(attempt.ResultPath));
        f.AssertEvent(attempt.AttemptId, "solo", goal, goal);
        Assert.Empty(f.Solo.GetAllCapacityReservingAttempts());
    }

    [Theory]
    [InlineData("result")]
    [InlineData("exit")]
    public void SoloAttempt_PublishedArtifactAndMovedHead_PreservesPendingCompletion(string artifact)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        Assert.True(f.Kernel.BeginGoalAcceptanceVerification(goal.Id, "gate"));
        var attempt = f.Single(goal);
        var artifactPath = artifact == "result" ? attempt.ResultPath : attempt.ExitCodePath;
        var artifactContent = artifact == "result" ? "{}" : "0";
        File.WriteAllText(artifactPath, artifactContent);
        Assert.Equal(attempt.AttemptId, Assert.Single(f.Solo.GetAllCapacityReservingAttempts()).AttemptId);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, Fixture.ReadSolo(attempt).Outcome);
        var before = File.ReadAllBytes(attempt.MetadataPath);
        var holderPhase = f.Lease.Read(AcceptanceHolderBinding.For(attempt)).Phase;
        f.Heads[goal.Id] = New;

        var result = f.Tick();

        Assert.Equal(before, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Equal(artifactContent, File.ReadAllText(artifactPath));
        Assert.Equal(holderPhase, f.Lease.Read(AcceptanceHolderBinding.For(attempt)).Phase);
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Empty(result.Progress);
        Assert.Empty(result.ChangedGoalIds);
        Assert.Empty(f.Reads);
        Assert.Empty(f.CancelledJobs);
        Assert.Empty(f.Events());
        Assert.Single(f.Solo.GetAllCapacityReservingAttempts());
    }

    [Fact]
    public void TrainAttempt_ThreeMembersOneMoved_StopsGroupedAttemptAndNamesMemberAndRevisions()
    {
        using var f = new Fixture();
        var members = new[] { f.Goal(), f.Goal(), f.Goal() };
        var attempt = f.Group("train", "three", members);
        Assert.True(f.Grouped.ReservesCapacity(attempt));
        var before = members.Select(g => JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(g.Id))).ToArray();
        f.Heads[members[1].Id] = New;

        var result = f.Tick();

        Assert.Equal(new[] { attempt.AttemptId }, f.Stops);
        var stopped = ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        Assert.Equal("Reconciled", stopped.Outcome);
        Assert.Equal($"refused:candidate-superseded member={members[1].Id.Value} gated={Old} head={New}", stopped.Detail);
        Assert.False(f.Grouped.ReservesCapacity(stopped));
        Assert.False(File.Exists(attempt.ResultPath));
        Assert.Empty(result.ChangedGoalIds);
        Assert.Equal(before, members.Select(g => JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(g.Id))));
        f.AssertEvent(attempt.AttemptId, "train", members[1], members);
        Assert.All(members, g => Assert.Equal(1, f.Reads[g.Id]));
        f.Tick();
        Assert.Single(f.Stops);
        Assert.Single(f.Events());
    }

    [Theory]
    [InlineData("solo")]
    [InlineData("train")]
    [InlineData("cohort")]
    public void MatchingHeads_AttemptKeepsRunning(string kind)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var path = kind == "solo" ? f.Single(goal).MetadataPath : f.Group(kind, "match", goal, f.Goal()).MetadataPath;
        var before = File.ReadAllBytes(path);

        var result = f.Tick();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(result.Progress);
        Assert.Empty(result.ChangedGoalIds);
        Assert.Empty(f.Stops);
        Assert.Empty(f.CancelledJobs);
        Assert.Empty(f.Events());
        Assert.All(f.Reads.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void UnreadableHead_KeepsAttemptRunningAndLogsOnceAcrossTicks()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Single(goal);
        f.Heads[goal.Id] = null;
        var before = File.ReadAllBytes(attempt.MetadataPath);

        var lines = Enumerable.Range(0, 3).SelectMany(_ => f.Tick().Progress).ToArray();

        Assert.Equal($"GATE_SUPERSESSION_HEAD_UNREADABLE attempt={attempt.AttemptId} kind=solo member={goal.Id.Value}", Assert.Single(lines));
        Assert.Equal(3, f.Reads[goal.Id]);
        Assert.Equal(before, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Empty(f.CancelledJobs);
        Assert.Empty(f.Events());
    }

    [Fact]
    public void UnreadableThenReadableMismatch_LaterTickStopsAttempt()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Single(goal);
        f.Heads[goal.Id] = null;
        Assert.Single(f.Tick().Progress);
        Assert.Empty(f.Tick().Progress);
        Assert.Empty(f.CancelledJobs);
        f.Heads[goal.Id] = New;

        f.Tick();

        Assert.Single(f.CancelledJobs);
        Assert.Equal(3, f.Reads[goal.Id]);
        f.AssertEvent(attempt.AttemptId, "solo", goal, goal);
    }

    [Fact]
    public void CohortAttempt_HeadMoved_StopsGroupedAttempt()
    {
        using var f = new Fixture();
        var a = f.Goal();
        var b = f.Goal();
        var attempt = f.Group("cohort", "pair", a, b);
        f.Heads[a.Id] = New;
        f.Heads[b.Id] = New;

        f.Tick();

        Assert.Equal(new[] { attempt.AttemptId }, f.Stops);
        Assert.False(f.Grouped.ReservesCapacity(ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath)));
        f.AssertEvent(attempt.AttemptId, "cohort", a, a, b);
    }

    [Fact]
    public void MixedUnreadableAndMoved_KeepsWholeAttemptRunning()
    {
        using var f = new Fixture();
        var moved = f.Goal();
        var unreadable = f.Goal();
        var attempt = f.Group("train", "mixed", moved, unreadable);
        f.Heads[moved.Id] = New;
        f.Heads[unreadable.Id] = null;
        var before = File.ReadAllBytes(attempt.MetadataPath);

        Assert.Single(f.Tick().Progress);
        Assert.Empty(f.Tick().Progress);

        Assert.Equal(before, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Empty(f.Stops);
        Assert.Empty(f.Events());
    }

    [Theory]
    [InlineData("solo")]
    [InlineData("train")]
    public void FailedStop_AppendsNoEventAndKeepsGoalVerifying(string kind)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        Assert.True(f.Kernel.BeginGoalAcceptanceVerification(goal.Id, "gate"));
        var path = kind == "solo" ? f.Single(goal, inProcess: true).MetadataPath : f.Group(kind, "failed", goal).MetadataPath;
        var before = File.ReadAllBytes(path);
        f.StopSucceeds = false;
        f.Heads[goal.Id] = New;

        var result = f.Tick();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(GoalStatus.Verifying, goal.Status);
        Assert.Empty(result.ChangedGoalIds);
        Assert.Empty(f.Events());
        Assert.Empty(f.CancelledJobs);
        if (kind == "train") Assert.Equal(new[] { "failed" }, f.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoAttemptsSharingGoal_ReadHeadOncePerTick(bool unreadable)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        f.Single(goal);
        f.Group("train", "shared", goal);
        f.Heads[goal.Id] = unreadable ? null : Old;

        f.Tick();
        Assert.Equal(1, f.Reads[goal.Id]);
        f.Tick();
        Assert.Equal(2, f.Reads[goal.Id]);
        Assert.Empty(f.Stops);
        Assert.Empty(f.Events());
    }

    [Fact]
    public void KnownSnapshotHead_DoesNotCallFallback()
    {
        var id = GoalId.New();
        var snapshotReads = 0;
        var heads = new GateCandidateHeadObservations(_ => { snapshotReads++; return Old; },
            _ => throw new InvalidOperationException("Snapshot must be reused"));
        Assert.Equal(Old, heads.Observe(id));
        Assert.Equal(Old, heads.Observe(id));
        Assert.Equal(1, snapshotReads);
    }

    [Fact]
    public void ThrowingHeadRead_IsMemoizedAsUnreadable()
    {
        var reads = 0;
        var id = GoalId.New();
        var heads = new GateCandidateHeadObservations(_ => null,
            _ => { reads++; throw new IOException("fake unreadable ref"); });
        Assert.Null(heads.Observe(id));
        Assert.Null(heads.Observe(id));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void TerminalGroupedAttempt_DoesNotReadHeadOrStop()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Group("train", "finished", goal);
        ConductorGroupedGateAttemptCoordinator.Save(attempt with { Outcome = "Completed" });
        f.Heads[goal.Id] = New;
        f.Tick();
        Assert.Empty(f.Reads);
        Assert.Empty(f.Stops);
        Assert.Empty(f.Events());
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Fixture.Now; }
    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);
        private static readonly DateTimeOffset Earlier = Now.AddMinutes(-1);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string root = Path.Combine(Path.GetTempPath(), "gate-supersession-" + Guid.NewGuid().ToString("N"));
        private readonly string eventPath;
        private readonly SpawnProcessIdentity holder = new(222, Earlier, "C:\\fake-dotnet.exe");
        private readonly SpawnProcessIdentity generation = new(111, Earlier, "C:\\fake-dotnet.exe");
        internal readonly AgentOrchestratorKernel Kernel = new(new Clock());
        internal readonly ConductorParallelAcceptanceAttemptCoordinator Solo;
        internal readonly ConductorGroupedGateAttemptCoordinator Grouped;
        internal readonly AcceptanceHolderLease Lease;
        internal readonly GateCandidateSupersessionStop Stopper;
        internal readonly List<string> Stops = [];
        internal readonly List<string> CancelledJobs = [];
        internal readonly Dictionary<GoalId, string?> Heads = [];
        internal readonly Dictionary<GoalId, int> Reads = [];
        internal bool StopSucceeds = true;

        internal Fixture()
        {
            Directory.CreateDirectory(root);
            eventPath = Path.Combine(root, "events.log");
            var cancelled = new HashSet<string>(StringComparer.Ordinal);
            Lease = new(utcNow: () => Now, classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                job: (job, _, _, cancel) =>
                {
                    if (cancel) { CancelledJobs.Add(job); cancelled.Add(job); }
                    return new(!cancelled.Contains(job), cancelled.Contains(job), "fake-job");
                }, attach: _ => new EmptyDisposable(), stopUnclaimed: _ => false);
            Solo = new(Path.Combine(root, "solo"), "main", utcNow: () => Now, isProcessAlive: _ => true,
                holderLease: Lease, generationIdentity: generation,
                tryAcquireAttemptWriterLease: (_, _) => new EmptyDisposable());
            Grouped = new(Path.Combine(root, "grouped"), generationId: 111, utcNow: () => Now,
                isProcessAlive: _ => true, classifyHolder: _ => SpawnTrackedProcessStatus.LiveMatch,
                stop: attempt => { Stops.Add(attempt.AttemptId); return StopSucceeds; }, generationIdentity: generation);
            Stopper = new(Solo, Grouped, new ConductEventLogWriter(eventPath, utcNow: () => Now), () => Now);
        }

        internal Goal Goal()
        {
            var goal = Kernel.CreateGoal("Member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(true, "Passed.", root, Now));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Heads[goal.Id] = Old;
            return goal;
        }

        internal GateCandidateSupersessionStop.Result Tick() => Stopper.Apply(Kernel,
            new GateCandidateHeadObservations(_ => null, id =>
            {
                Reads[id] = Reads.GetValueOrDefault(id) + 1;
                return Heads.GetValueOrDefault(id);
            }));

        internal ConductorGroupedGateAttempt Group(string kind, string id, params Goal[] members)
        {
            var dir = Path.Combine(root, "grouped", id);
            Directory.CreateDirectory(dir);
            var attempt = new ConductorGroupedGateAttempt(id, kind,
                members.Select(g => new ConductorGroupedGateMember(g.Id.Value, Old, "candidate", [], [], ChangeRiskTier.Behavior)).ToArray(),
                "main", "tree", "manifest", "identity-" + id, Earlier, holder.ProcessId, generation.ProcessId,
                Path.Combine(dir, id + ".attempt.json"), Path.Combine(dir, "result"), Path.Combine(dir, "exit"),
                Path.Combine(dir, "out"), Path.Combine(dir, "err"), root, "{}", Earlier, holder.ImagePath);
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            return attempt;
        }

        internal ConductorParallelAcceptanceAttempt Single(Goal goal, bool inProcess = false)
        {
            var id = "solo-" + goal.Id.Value[..8];
            var dir = Path.Combine(root, "solo", goal.Id.Value);
            Directory.CreateDirectory(dir);
            var attempt = new ConductorParallelAcceptanceAttempt(id, goal.Id.Value, goal.Id.Value[..8], 0, Old, "main", Earlier, Earlier,
                holder.ProcessId, ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(dir, id + ".out"), Path.Combine(dir, id + ".err"), Path.Combine(dir, id + ".exit"), Path.Combine(dir, id + ".heartbeat"),
                Path.Combine(dir, id + ".result"), Path.Combine(dir, id + ".attempt.json"),
                ExecutionProtocol: inProcess ? "in-process" : "out-of-process")
            { HolderContractVersion = 1, OwnerProcessStartedAt = holder.StartedAt, OwnerExecutablePath = holder.ImagePath };
            File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, Json));
            var binding = AcceptanceHolderBinding.For(attempt);
            Lease.Reserve(binding, generation);
            using (Lease.Claim(binding, holder)) { }
            return attempt;
        }

        internal static ConductorParallelAcceptanceAttempt ReadSolo(ConductorParallelAcceptanceAttempt attempt) =>
            JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(attempt.MetadataPath), Json)!;

        internal ConductEventRecord[] Events() => !File.Exists(eventPath) ? [] : File.ReadLines(eventPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!).Where(e => e.EventKind == "gate-cancel").ToArray();

        internal void AssertEvent(string attempt, string kind, Goal moved, params Goal[] members)
        {
            var record = Assert.Single(Events());
            Assert.Equal(Now, record.Timestamp);
            Assert.Equal(moved.Id.Value, record.GoalId);
            Assert.Equal($"GATE_CANCELLED attempt={attempt} kind={kind} members={string.Join('+', members.Select(g => g.Id.Value))} " +
                $"reason=candidate-superseded member={moved.Id.Value} gated={Old} head={New}", record.Detail);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
    }
}
