using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel safe: private storage roots and execution locks; injected launches never spawn a process.
public sealed class PreReviewEvidenceLaunchCapacityTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    public void BusySnapshot_RefusesBeforeAttemptSideEffects(int unheld, int waiting)
    {
        using var fixture = new Fixture(new(unheld, 2, waiting, true), durableHolder: true);

        var exception = Assert.Throws<PreReviewEvidenceBuildSlotsBusyException>(() => fixture.Start());

        Assert.Equal(fixture.Goal.Id, exception.GoalId);
        Assert.Equal(unheld, exception.UnheldPermits);
        Assert.Equal(2, exception.TotalPermits);
        Assert.Equal(waiting, exception.WaitingTickets);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(0, fixture.Launches);
        fixture.AssertNoAttemptArtifacts();
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(0, 0, false)]
    public void AvailableOrUnreadableSnapshot_LaunchesOnce(int unheld, int waiting, bool readable)
    {
        using var fixture = new Fixture(new(unheld, 2, waiting, readable));

        var decision = fixture.Start();

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, decision.Attempt.Outcome);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.Launches);
        Assert.Single(fixture.Files("*.attempt.json"));
        Assert.Single(fixture.Coordinator.GetUnreconciledAttempts([fixture.Goal.Id.Value]));
    }

    [Fact]
    public void GateKind_WithBusySnapshot_LaunchesWithoutReadingCapacity()
    {
        using var fixture = new Fixture(new(0, 2, 0, true));

        var decision = fixture.Coordinator.Evaluate(fixture.Candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("The launch delegate must not run the gate."));

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(0, fixture.Reads);
        Assert.Single(fixture.Files("*.attempt.json"));
    }

    [Fact]
    public void ReservedSuccessor_WithBusySnapshot_LaunchesWithoutAnotherCapacityRead()
    {
        using var fixture = new Fixture(new(1, 2, 0, true));
        var first = fixture.Start();
        fixture.Coordinator.RunAttemptForTests(first.Attempt, fixture.Candidate, ConductorAutonomyPolicy.Permissive,
            (candidate, _, _, _, _) => ConductorParallelAcceptanceRunResult.Fault(candidate,
                new OperationCanceledException("cancelled")));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Cancelled,
            fixture.Coordinator.TryReadLatest(fixture.Goal.Id.Value)!.Outcome);
        fixture.Snapshot = new(0, 2, 0, true);
        var nextCandidate = ConductorParallelAcceptanceCandidate.Create(fixture.Goal, 0, [], "branch-2", "main-1");

        var next = fixture.Start(nextCandidate);

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, next.Kind);
        Assert.NotEqual(first.Attempt.AttemptId, next.Attempt.AttemptId);
        Assert.Equal(PreReviewEvidenceRelaunchTrigger.TerminalWithoutRunReplaced, next.Attempt.RelaunchProvenance!.Trigger);
        Assert.Equal(2, fixture.Launches);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(2, fixture.Files("*.attempt.json").Length);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, false)]
    public void RetainedTicket_CountsOnlyPredecessors(bool ownFirst, int waiting, bool mayStart)
    {
        using var fixture = new Fixture(new(0, 2, 0, true));
        var clock = new RecordingTimeProvider();
        var queue = new BuildPermitWaitQueue(fixture.Storage, clock);
        var ownKey = FocusedEvidencePermitTicket.Key(fixture.Goal.Id);
        var own = new BuildPermitClaimant(ownKey, BuildPermitClaimantKind.FocusedEvidence, Environment.ProcessId, true);
        var other = new BuildPermitClaimant("gate:other", BuildPermitClaimantKind.Gate, Environment.ProcessId, true);
        queue.Register(ownFirst ? own : other);
        clock.Advance(TimeSpan.FromSeconds(1));
        queue.Register(ownFirst ? other : own);
        var before = queue.Snapshot().ToArray();
        var heldPath = DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(0, fixture.Storage);

        var snapshot = WorkerBuildCapacityGate.ReadHostSnapshot(fixture.Storage, clock, path => path == heldPath, ownKey);

        Assert.Equal(new WorkerBuildCapacitySnapshot(1, 2, waiting, true), snapshot);
        Assert.Equal(mayStart, PreReviewEvidenceLaunchCapacity.MayStart(snapshot));
        var unclaimed = WorkerBuildCapacityGate.ReadHostSnapshot(fixture.Storage, clock, path => path == heldPath);
        Assert.Equal(new WorkerBuildCapacitySnapshot(1, 2, 2, true), unclaimed);
        Assert.False(PreReviewEvidenceLaunchCapacity.MayStart(unclaimed));
        var absent = WorkerBuildCapacityGate.ReadHostSnapshot(fixture.Storage, clock, path => path == heldPath, "absent");
        Assert.Equal(unclaimed, absent);
        Assert.Equal(before, queue.Snapshot().ToArray());
    }

    [Fact]
    public void ProductionFactory_WithBothPermitsHeld_RefusesBeforeAttemptOrTicket()
    {
        using var fixture = new Fixture(new(0, 2, 0, true));
        var workspace = new OrchestratorWorkspace(fixture.Root, fixture.Root, Path.Combine(fixture.Root, "state"),
            "default", false, "default", false, "", "", "", "", "", "", "");
        Directory.CreateDirectory(Path.Combine(fixture.Storage.RootPath, "build-slots"));
        using var first = HoldPermit(0);
        using var second = HoldPermit(1);
        var coordinator = OwnedChildAttemptCoordinatorFactory.CreateFocusedEvidence(workspace, fixture.Storage);

        var exception = Assert.Throws<PreReviewEvidenceBuildSlotsBusyException>(() =>
            coordinator.EvaluateFocusedEvidence(fixture.Candidate, ConductorAutonomyPolicy.Permissive,
                "focused tests", (_, _, _, _) => throw new InvalidOperationException("Evidence must not run.")));

        Assert.Equal(0, exception.UnheldPermits);
        Assert.Equal(0, exception.WaitingTickets);
        Assert.Empty(coordinator.GetUnreconciledAttempts([fixture.Goal.Id.Value]));
        fixture.AssertNoAttemptArtifacts();

        FileStream HoldPermit(int index)
        {
            var stream = new FileStream(DotnetBuildEnvironmentManager.BuildSlotExecutionLockPath(index, fixture.Storage),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Lock(0, 1);
            return stream;
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("evidence-capacity-").FullName;
        internal DotnetBuildStorageRoot Storage { get; }
        internal Goal Goal { get; } = new AgentOrchestratorKernel().CreateGoal("Check evidence launch capacity");
        internal ConductorParallelAcceptanceCandidate Candidate { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal WorkerBuildCapacitySnapshot Snapshot;
        internal int Reads;
        internal int Launches;

        internal Fixture(WorkerBuildCapacitySnapshot snapshot, bool durableHolder = false)
        {
            Snapshot = snapshot;
            Storage = new(Path.Combine(Root, "storage"));
            Candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], "branch-1", "main-1");
            Coordinator = new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    Launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7111);
                },
                acquireStableSlotLease: (_, _) => null,
                buildStorageRoot: Storage,
                holderLease: durableHolder ? new AcceptanceHolderLease() : null,
                readPreReviewEvidenceCapacity: goalId =>
                {
                    Assert.Equal(Goal.Id, goalId);
                    Reads++;
                    return Snapshot;
                });
        }

        internal ConductorParallelAcceptanceAttemptDecision Start(ConductorParallelAcceptanceCandidate? candidate = null) =>
            Coordinator.EvaluateFocusedEvidence(candidate ?? Candidate, ConductorAutonomyPolicy.Permissive,
                "focused tests", (_, _, _, _) => throw new InvalidOperationException("The launch delegate must not run evidence."));

        internal string[] Files(string pattern) => Directory.GetFiles(Root, pattern, SearchOption.AllDirectories);

        internal void AssertNoAttemptArtifacts()
        {
            Assert.Empty(Files("*.attempt.json"));
            Assert.Empty(Files("attempt-sequence.txt"));
            Assert.Empty(Files("*.holder.json"));
            Assert.Empty(Coordinator.GetUnreconciledAttempts([Goal.Id.Value]));
            var tickets = Path.Combine(Storage.RootPath, "build-slots", "wait-tickets");
            Assert.Empty(Directory.Exists(tickets) ? Directory.GetFiles(tickets, "*.json") : []);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
