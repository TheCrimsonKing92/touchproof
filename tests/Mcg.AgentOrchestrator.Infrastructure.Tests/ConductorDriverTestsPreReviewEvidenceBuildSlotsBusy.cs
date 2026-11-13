using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel safe: in-memory goal, private files, injected liveness, capacity and process launcher.
public sealed class ConductorDriverTestsPreReviewEvidenceBuildSlotsBusy
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void BusyCapacity_HoldsWithoutAttemptOrStateChange_ThenNextTickStarts()
    {
        using var fixture = new EvidenceFixture();
        var state = fixture.State;
        var goalStatus = fixture.Goal.Status;
        var taskStatuses = fixture.Goal.Tasks.Select(task => task.Status).ToArray();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver.AdvanceOnce(
            fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(state, held.State);
        Assert.Equal(state, fixture.State);
        Assert.Equal(goalStatus, fixture.Goal.Status);
        Assert.Equal(taskStatuses, fixture.Goal.Tasks.Select(task => task.Status).ToArray());
        Assert.Null(held.StableIdentity);
        Assert.Equal(ConductorHoldOwner.None, held.Owner);
        AssertCounts(held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("pre-review-evidence", decision.Stage);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal(4, decision.Rung);
        Assert.Equal("build-slots-busy", decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        Assert.Equal(new PolicyDecisionFact[]
        {
            new("goalId", fixture.Goal.Id.Value),
            new("condition", "build-slots-busy"),
            new("attemptId", ""),
            new("attemptOutcome", ""),
            new("holdReason", held.Reason)
        }, decision.Facts.ToArray());
        var payload = VerifiedAcceptanceEscalationDecision.BuildTickOutcomePayload(held);
        Assert.Equal("Held", payload.OutcomeKind);
        var recorded = Assert.IsType<PolicyDecisionRecord>(payload.Decision);
        Assert.Equal(decision.Facts.ToArray(), recorded.Facts.ToArray());
        fixture.AssertNoAttempt();

        fixture.Snapshot = new(2, 2, 0, true);
        var started = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Driver.AdvanceOnce(
            fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        var attempt = Assert.Single(fixture.Coordinator.GetUnreconciledAttempts([fixture.Goal.Id.Value]));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, attempt.Outcome);
        Assert.Equal(2, Assert.IsType<PolicyDecisionRecord>(started.Decision).Rung);
        Assert.Equal($"pre-review-evidence:{attempt.AttemptId}", started.StableIdentity);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, started.Owner);
        Assert.Equal(1, fixture.Launches);
        Assert.Equal(2, fixture.Reads);
        Assert.Single(Directory.GetFiles(fixture.Root, "*.attempt.json", SearchOption.AllDirectories));
        Assert.Equal(state, fixture.State);
        Assert.Equal(taskStatuses, fixture.Goal.Tasks.Select(task => task.Status).ToArray());
        Assert.Null(fixture.Reviewer.PreReviewEvidenceReceipt);
        Assert.Equal(0, fixture.Runs);
        Assert.Equal(0, fixture.Dispatches);
    }

    [Fact]
    public void FindingEvidenceCaller_BusyCapacity_ReturnsPendingWithoutAttempt()
    {
        using var fixture = new EvidenceFixture();
        var state = fixture.State;
        var status = fixture.Reviewer.Status;

        var completed = fixture.Driver.TryReconcileFocusedEvidenceAttempt(fixture.Goal,
            ConductorAutonomyPolicy.Permissive, "focused tests", "capacity-sha", "finding",
            null, out var evidence, out var attempt, out var observation, out var attemptKind);

        Assert.False(completed);
        Assert.Null(evidence);
        Assert.Null(attempt);
        Assert.Null(attemptKind);
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observation.Kind);
        Assert.Equal(ConductorHoldOwner.None, observation.HoldOwner);
        AssertCounts(observation.Evidence);
        Assert.Equal(state, fixture.State);
        Assert.Equal(status, fixture.Reviewer.Status);
        Assert.Equal(1, fixture.Reads);
        fixture.AssertNoAttempt();
    }

    private static void AssertCounts(string detail)
    {
        Assert.Contains("unheld=1/2", detail);
        Assert.Contains("waiting=1", detail);
        Assert.Contains("retry on next conduct tick", detail);
    }

    private sealed class EvidenceFixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal Goal Goal { get; }
        internal TaskSpec Reviewer { get; }
        internal ConductorDriver Driver { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Coordinator { get; }
        internal WorkerBuildCapacitySnapshot Snapshot = new(1, 2, 1, true);
        internal int Reads;
        internal int Launches;
        internal int Runs;
        internal int Dispatches;
        internal GoalLifecycleState State => GoalLifecycle.ResolveState(Goal, Driver.GetFacts(Goal));

        internal EvidenceFixture()
        {
            var (kernel, goal) = SoftwareGoal();
            Goal = goal;
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.TakeWhile(task => task.Id != Reviewer.Id))
                PassVerification(kernel, Goal, task);
            Coordinator = new(Path.Combine(Root, "attempts"), TrunkBranchName.Default,
                executionDirectory: Root,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    Launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7111);
                },
                acquireStableSlotLease: (_, _) => null,
                readPreReviewEvidenceCapacity: goalId =>
                {
                    Assert.Equal(Goal.Id, goalId);
                    Reads++;
                    return Snapshot;
                });
            Driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("capacity-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    Runs++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) => kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    Dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: Coordinator,
                executionDirectory: Root);
        }

        internal void AssertNoAttempt()
        {
            Assert.Empty(Coordinator.GetUnreconciledAttempts([Goal.Id.Value]));
            Assert.Empty(Directory.GetFiles(Root, "*.attempt.json", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Root, "attempt-sequence.txt", SearchOption.AllDirectories));
            Assert.Null(Reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(0, Launches);
            Assert.Equal(0, Runs);
            Assert.Equal(0, Dispatches);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
