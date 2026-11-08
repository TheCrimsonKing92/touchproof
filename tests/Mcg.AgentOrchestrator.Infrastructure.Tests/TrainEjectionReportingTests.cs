using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns an in-memory kernel and a unique directory for SQLite and evidence.
public sealed class TrainEjectionReportingTests
{
    [Theory]
    [InlineData(MergeTrainGateOutcome.Passed, "passed")]
    [InlineData(MergeTrainGateOutcome.Failed, "failed")]
    public void CompletedRetry_ReportsReceiptProducingTrain(MergeTrainGateOutcome outcome, string verdict)
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var surviving = fixture.CompleteRetry(outcome);

        var line = fixture.Reconcile(surviving, verdict);

        Assert.Equal(fixture.ExpectedLine(surviving, verdict) +
            $" ejected={fixture.Goals[2].Id.Value}:RedNewestMember", line);
        Assert.DoesNotContain($"identity={fixture.Original.Value}", line);
        Assert.DoesNotContain($"members={fixture.OriginalMembers}", line);
    }

    [Fact]
    public void EjectedMember_HoldRefreshTracksPendingThenSurvivingReceipt()
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();

        var pending = fixture.Hold(2);
        Assert.Equal("Acceptance cohort gate owns this member: " +
            $"ejected from train {fixture.Original.Value} reason=RedNewestMember surviving-receipt=pending",
            pending.Reason);
        Assert.DoesNotContain("owner=", pending.Reason);
        Assert.DoesNotContain("fingerprint=", pending.Reason);

        var surviving = fixture.CompleteRetry();
        var completed = fixture.Hold(2);
        Assert.Equal("Acceptance cohort gate owns this member: " +
            $"ejected from train {fixture.Original.Value} reason=RedNewestMember surviving-receipt={surviving.ReceiptId}",
            completed.Reason);
        Assert.Equal(completed.Reason, fixture.Hold(2).Reason);
        Assert.Equal(ConductorHoldOwner.BackgroundAttempt, completed.Owner);
        Assert.Equal(pending.Owner, completed.Owner);
    }

    [Fact]
    public void Survivor_HoldNamesReducedMembersAndReceiptIdentity()
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var surviving = fixture.CompleteRetry();

        var held = fixture.Hold(0);

        Assert.Equal("Acceptance cohort gate owns this member: outcome=inflight " +
            $"owner={string.Join('+', fixture.Goals.Take(2).Select(goal => goal.Id.Value).Order(StringComparer.Ordinal))} " +
            $"fingerprint=train:{surviving.Identity.Value} elapsed_ms=180000", held.Reason);
        Assert.DoesNotContain(fixture.Original.Value, held.Reason);
        Assert.DoesNotContain(fixture.Goals[2].Id.Value, held.Reason);
    }

    [Fact]
    public void NoEjection_CompletionAndHoldRemainByteIdentical()
    {
        using var fixture = new Fixture(initialPassed: true);

        Assert.Equal("Acceptance cohort gate owns this member: " + fixture.InFlightDetail, fixture.Hold(0).Reason);
        Assert.Equal(fixture.ExpectedLine(fixture.Initial, "passed"), fixture.Reconcile(fixture.Initial, "passed"));
    }

    [Fact]
    public void OtherAttemptReceipt_DoesNotReplacePendingSurvivingReceipt()
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var unrelated = fixture.CompleteRetry(attemptId: "unrelated-attempt");

        Assert.EndsWith("surviving-receipt=pending", fixture.Hold(2).Reason);
        Assert.Equal(fixture.ExpectedLine(unrelated, "passed", originalMembers: true),
            fixture.Reconcile(unrelated, "passed"));
    }

    [Fact]
    public void CompletedTrain_SweptBeforeAdmission_RetainsEjectionOnWaitingHolds()
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var surviving = fixture.CompleteRetry();
        Assert.EndsWith($"surviving-receipt={surviving.ReceiptId}", fixture.Hold(2).Reason);
        fixture.CompleteAndSweep(surviving);

        var first = fixture.RefreshWaitingHold();
        Assert.Equal($"ejected from train {fixture.Original.Value} reason=RedNewestMember " +
            $"surviving-receipt={surviving.ReceiptId}", first.Reason);
        Assert.DoesNotContain("owner=", first.Reason);
        Assert.DoesNotContain("fingerprint=", first.Reason);
        Assert.Equal(first, fixture.RefreshWaitingHold());
        Assert.Equal(first.Reason, fixture.RefreshWaitingHold(stableIdentity: null).Reason);
        Assert.False(TrainEjectionReport.TryGetCohortMemberHold(fixture.Driver, fixture.Goals[2].Id, out _));
    }

    [Theory]
    [InlineData(GoalLifecycleState.Failed)]
    [InlineData(GoalLifecycleState.Verified)]
    public void StalledBuildHold_AfterEjection_QuotesLiveReason(GoalLifecycleState state)
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        // Prove the retained receipt is eligible before replacing the live train wait.
        Assert.Contains("ejected from train", fixture.RefreshWaitingHold().Reason);
        var held = fixture.CompletionBuildHold(state);
        var result = new ConductorAdvanceResult(fixture.Goals[2].Id.Value, "42c77c56", "test", held);
        Assert.Same(result, TrainEjectionReport.DescribeWaitingResult(fixture.Driver, fixture.Goals[2].Id, result));

        var blocker = fixture.AssertStalledBlocker(held);

        Assert.Equal(held.Decision!.Reason, held.Reason);
        Assert.Equal(held.Reason, blocker);
        Assert.DoesNotContain("ejected from train", blocker);
        Assert.DoesNotContain("RedNewestMember", blocker);
    }

    [Fact]
    public void StalledTrainWait_AfterEjection_QuotesEjection()
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified,
            Fixture.WaitingReason, "capacity");

        var blocker = fixture.AssertStalledBlocker(held);

        Assert.Equal($"ejected from train {fixture.Original.Value} reason=RedNewestMember " +
            "surviving-receipt=pending", blocker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReAdmission_AfterSweep_DoesNotRestorePreviousEjection(bool grouped)
    {
        using var fixture = new Fixture();
        fixture.EjectNewest();
        var surviving = fixture.CompleteRetry();
        fixture.CompleteAndSweep(surviving);
        Assert.Contains("ejected from train", fixture.RefreshWaitingHold().Reason);

        fixture.Readmit(grouped);

        Assert.Equal(Fixture.WaitingReason, fixture.RefreshWaitingHold().Reason);
        // An ordinary refresh and late reconciliation of the old attempt cannot revive its text.
        fixture.Coordinator.TryReconcileDead(fixture.Attempt, "child-result-published");
        fixture.EjectNewest();
        Assert.Equal(Fixture.WaitingReason, fixture.RefreshWaitingHold().Reason);
        Assert.Equal(fixture.InFlightDetail, TrainEjectionReport.DescribeHold(
            fixture.Root, fixture.Goals[2].Id, fixture.InFlightDetail));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("capacity")]
    public void WaitingHold_ReportingRefreshPreservesTrackingIdentities(string? stableIdentity)
    {
        using var fixture = new Fixture();
        var goal = fixture.Goals[2];
        fixture.RefreshWaitingHold(stableIdentity, DateTimeOffset.UnixEpoch.AddMinutes(3));
        var landing = Assert.IsType<GoalLandingBlocker>(goal.LandingBlocker);
        var hold = Assert.IsType<GoalHoldState>(goal.CurrentHold);

        fixture.EjectNewest();
        var pending = fixture.RefreshWaitingHold(stableIdentity, DateTimeOffset.UnixEpoch.AddMinutes(4));
        Assert.EndsWith("surviving-receipt=pending", pending.Reason);
        AssertTrackingUnchanged();

        var surviving = fixture.CompleteRetry();
        fixture.CompleteAndSweep(surviving);
        var completed = fixture.RefreshWaitingHold(stableIdentity, DateTimeOffset.UnixEpoch.AddMinutes(5));
        Assert.EndsWith($"surviving-receipt={surviving.ReceiptId}", completed.Reason);
        AssertTrackingUnchanged();

        fixture.Readmit(grouped: false);
        var readmitted = fixture.RefreshWaitingHold(stableIdentity, DateTimeOffset.UnixEpoch.AddMinutes(6));
        Assert.Equal(Fixture.WaitingReason, readmitted.Reason);
        AssertTrackingUnchanged();

        void AssertTrackingUnchanged()
        {
            Assert.Equal(landing.Identity, goal.LandingBlocker!.Identity);
            Assert.Equal(landing.Since, goal.LandingBlocker.Since);
            Assert.Equal(hold.Identity, goal.CurrentHold!.Identity);
            Assert.Equal(hold.StartedAt, goal.CurrentHold.StartedAt);
        }
    }

    [Fact]
    public void DuplicateAndForeignEjections_ReportOnceWithRecordedReason()
    {
        using var fixture = new Fixture();
        fixture.Store.RecordEjections(Fixture.AttemptId,
        [
            new(fixture.Goals[2].Id, MergeTrainEjectionReason.RebaseConflict, [], "first"),
            new(GoalId.New(), MergeTrainEjectionReason.StaleBinding, [], "not in this train")
        ]);
        fixture.EjectNewest();
        var surviving = fixture.CompleteRetry();

        Assert.Equal(fixture.ExpectedLine(surviving, "passed") +
            $" ejected={fixture.Goals[2].Id.Value}:RedNewestMember", fixture.Reconcile(surviving, "passed"));
    }

    [Fact]
    public void UnreadableStore_LeavesExistingReportingAvailable()
    {
        using var fixture = new Fixture();
        var broken = Path.Combine(fixture.Root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "merge-train-acceptance.db"), "not sqlite");

        Assert.Null(TrainEjectionReport.DescribeCompletion(broken, fixture.Attempt, fixture.Initial.ReceiptId));
        Assert.Equal(fixture.InFlightDetail,
            TrainEjectionReport.DescribeHold(broken, fixture.Goals[2].Id, fixture.InFlightDetail));
    }

    private sealed class Fixture : IDisposable
    {
        internal const string AttemptId = "red-then-retry";
        internal const string WaitingReason = "Acceptance capacity is occupied; retry next tick.";
        private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch;
        private readonly string _worktree = Path.Combine(Path.GetTempPath(), $"train-report-{Guid.NewGuid():N}");
        private readonly AgentOrchestratorKernel _kernel = new();
        private bool _completed;
        internal string Root { get; }
        internal ConductorDriver Driver { get; }
        internal ConductorGroupedGateAttemptCoordinator Coordinator { get; }
        internal Goal[] Goals { get; }
        internal MergeTrainIdentity Original { get; }
        internal MergeTrainReceipt Initial { get; }
        internal MergeTrainAcceptanceStore Store { get; }
        internal ConductorGroupedGateAttempt Attempt { get; }
        internal string OriginalMembers => string.Join('+', Goals.Select(goal => goal.Id.Value));
        internal string InFlightDetail => "outcome=inflight owner=" +
            string.Join('+', Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal)) +
            $" fingerprint=train:{Original.Value} elapsed_ms=180000";

        internal Fixture(bool initialPassed = false)
        {
            var workspace = OrchestratorWorkspace.ForDirectory(_worktree);
            Root = workspace.OrchestratorDirectory;
            InfrastructureTestSupport.SetUpStateDatabase(workspace.SqliteStatePath);
            Goals = new[] { "b3c41184", "a837a8a0", "42c77c56" }.Select(prefix =>
                _kernel.CreateGoal(new GoalId(prefix.PadRight(32, '0')), $"Member {prefix}")).ToArray();
            var bindings = Goals.Select((goal, index) => new MergeTrainMemberBinding(goal.Id,
                new string('b', 40), new string('b', 40), [$"src/Member{index}.cs"], [],
                ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected",
                new string('c', 40))).ToArray();
            Original = MergeTrainIdentity.Create(bindings, new string('a', 40), new string('c', 40), "manifest");
            Store = new MergeTrainAcceptanceStore(Path.Combine(Root, "merge-train-acceptance.db"));
            Initial = Save(new("original-receipt", Original,
                initialPassed ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.Failed,
                Started.AddMinutes(1), 10, initialPassed ? [] : ["gate"], initialPassed ? 0 : 1, [])
                { GateAttemptId = AttemptId });
            Attempt = new("child-attempt", "train", bindings.Select(member => new ConductorGroupedGateMember(
                    member.GoalId.Value, member.BranchRevision, member.CandidateRevision, member.LandingPaths,
                    member.ResourceKeys, member.ChangeRiskTier)).ToArray(),
                Original.ObservedMainRevision, Original.TrainTreeRevision, Original.ManifestIdentity, Original.Value,
                Started, 4242, 91001, Path.Combine(Root, "grouped-gate-attempts", "original.attempt.json"), Path.Combine(Root, "result.json"),
                Path.Combine(Root, "exit"), Path.Combine(Root, "out.log"), Path.Combine(Root, "err.log"),
                _worktree, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
            Coordinator = new(Path.Combine(Root, "grouped-gate-attempts"), generationId: 91001,
                isProcessAlive: _ => !_completed, utcNow: () => Started.AddMinutes(3),
                stateDirectory: Root, eventSink: _ => { });
            Driver = new(_kernel, workspace, new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null)),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), utcNow: () => Started.AddMinutes(3));
            Driver.EnableOwnedGroupedGateAttempts(Coordinator);
            Assert.Equal(3, Store.TryReadReceipt(Original.Value)!.Identity.Members.Count);
        }

        internal void EjectNewest() => Store.RecordEjections(AttemptId,
            [new(Goals[2].Id, MergeTrainEjectionReason.RedNewestMember, [], "Dropped after RED receipt original-receipt.")]);

        internal ConductorAdvanceOutcome.Held CompletionBuildHold(GoalLifecycleState state)
        {
            var goal = Goals[2];
            var task = _kernel.AddTask(goal.Id, AgentRole.Developer, "Retained candidate edits");
            _kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            _kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test.exe", _worktree, Started));
            _kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", _worktree, 1, "", "build evidence unavailable", Started,
                    OrchestratorFailureReason: "completion-build-coordinator-unavailable"));
            _kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Build evidence required");
            _kernel.EscalateTaskFailure(goal.Id, task.Id, "Completion build coordinator unavailable");
            return Assert.IsType<ConductorAdvanceOutcome.Held>(WorkerBuildEvidenceRecoveryHold.Decide(
                goal, state, new FailedGoalRecoveryIdentity(goal.Id, task.Id, null, "fixture")));
        }

        internal string AssertStalledBlocker(ConductorAdvanceOutcome.Held held)
        {
            Assert.NotNull(Driver.ExecutionDirectory);
            var goal = Goals[2];
            var timeline = new GoalLifecycleEventWriter(Path.Combine(Root, "timeline"));
            Driver.HoldEscalationEventWriter = timeline;
            var conduct = new ConductEventLogWriter(Path.Combine(Root, "conduct.log"), utcNow: () => Started);
            var changed = new HashSet<GoalId>();
            var lines = new List<string>();
            foreach (var minutes in new[] { 5, 16, 27 })
                ConductorBatchLoop.TrackGoalOutcome(_kernel, Driver, goal, held, Started.AddMinutes(minutes),
                    TimeSpan.FromMinutes(10), changed, lines, conduct);

            Assert.NotNull(goal.CurrentHold?.StalledAt);
            Assert.Single(lines, line => line.StartsWith("GOAL_STALLED ", StringComparison.Ordinal));
            var escalation = ReadEscalation(timeline.EventFilePath(goal.Id), "eventType", "GoalEscalated", "reason");
            Assert.Equal(escalation, ReadEscalation(conduct.CurrentPath, "eventKind", "goal-escalation", "detail"));
            Assert.StartsWith("ownerless-hold-stalled ", escalation);
            Assert.Contains("blocker=", escalation);
            var blocker = escalation[(escalation.IndexOf("blocker=", StringComparison.Ordinal) + "blocker=".Length)..];
            Assert.Equal(goal.CurrentHold!.Blocker, blocker);
            return blocker;
        }

        private static string ReadEscalation(string path, string key, string value, string textKey)
        {
            var entries = new List<string>();
            foreach (var line in File.ReadAllLines(path))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.GetProperty(key).GetString() == value)
                    entries.Add(document.RootElement.GetProperty(textKey).GetString()!);
            }
            return Assert.Single(entries);
        }

        internal MergeTrainReceipt CompleteRetry(MergeTrainGateOutcome outcome = MergeTrainGateOutcome.Passed,
            string attemptId = AttemptId) => Save(new("surviving-receipt",
                MergeTrainIdentity.Create(Original.Members.Take(2).ToArray(), Original.ObservedMainRevision,
                    new string('f', 40), Original.ManifestIdentity),
                outcome, Started.AddMinutes(2), 10, outcome == MergeTrainGateOutcome.Passed ? [] : ["gate"],
                outcome == MergeTrainGateOutcome.Passed ? 0 : 1, []) { GateAttemptId = attemptId });

        private MergeTrainReceipt Save(MergeTrainReceipt receipt) =>
            MergeTrainDropBacktestTests.Fixture.Save(Store, Root, receipt);

        internal ConductorAdvanceOutcome.Held Hold(int index)
        {
            Assert.True(TrainEjectionReport.TryGetCohortMemberHold(Driver, Goals[index].Id, out var detail));
            return Assert.IsType<ConductorAdvanceOutcome.Held>(ConductorBatchLoop.CohortMemberHeld(
                Goals[index], ConductorAutonomyPolicy.Conservative, detail).Outcome);
        }

        internal void CompleteAndSweep(MergeTrainReceipt receipt)
        {
            Assert.True(Driver.TryGetCohortGateHold(Goals[2].Id, out _));
            _ = Reconcile(receipt, "passed");
            _completed = true;
            Assert.False(Driver.TryGetCohortGateHold(Goals[2].Id, out _));
            Assert.Empty(Driver.GetActiveCohortGateMemberGoalIds());
        }

        internal ConductorAdvanceOutcome.Held RefreshWaitingHold(string? stableIdentity = "capacity",
            DateTimeOffset? observedAt = null)
        {
            var goal = Goals[2];
            var now = observedAt ?? Started.AddMinutes(4);
            var original = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, WaitingReason, stableIdentity);
            var results = new Dictionary<string, ParallelLandingOutcome>
            {
                [goal.Id.Value] = new(new(goal.Id.Value, goal.Id.Value[..8], "test", original), null)
            };
            Assert.True(TrainEjectionReport.RefreshHold(Driver, goal.Id, results));
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(results[goal.Id.Value].Result.Outcome);
            Assert.Equal(original, held with { Reason = WaitingReason });
            var before = _kernel.ObserveGoalHold(goal.Id, "Verified", WaitingReason, now,
                TimeSpan.FromHours(1), stableIdentity).Hold;
            ConductorBatchLoop.TrackGoalOutcome(_kernel, Driver, goal, held, now,
                TimeSpan.FromHours(1), [], []);
            Assert.Equal(held.Reason, goal.CurrentHold!.Blocker);
            Assert.Equal(before.Identity, goal.CurrentHold.Identity);
            return held;
        }

        internal void Readmit(bool grouped)
        {
            if (grouped)
            {
                var coordinator = new ConductorGroupedGateAttemptCoordinator(Path.Combine(Root, "grouped-gate-attempts"),
                    generationId: 91001, launch: _ => new(4243, null, null), isProcessAlive: _ => true,
                    utcNow: () => Started.AddMinutes(5), stateDirectory: Root);
                var attempt = coordinator.Create("train", Attempt.Members.Select(member =>
                    member.ToProjection(Attempt.MainRevision)).ToArray(), Attempt.MainRevision,
                    new string('d', 40), Attempt.ManifestIdentity, "readmitted-train", _worktree,
                    ConductorAutonomyPolicy.Conservative);
                // A queued candidate is not admission.
                ConductorGroupedGateAttemptCoordinator.Save(attempt);
                Assert.Contains("ejected from train", RefreshWaitingHold().Reason);
                var launched = coordinator.Launch(attempt);
                Assert.Equal(4243, launched.OwnerProcessId);
                return;
            }
            var solo = new ConductorParallelAcceptanceAttemptCoordinator(Path.Combine(Root, "acceptance-gate-attempts"),
                "main", runInline: true, utcNow: () => Started.AddMinutes(5), acquireStableSlotLease: (_, _) => null);
            var candidate = ConductorParallelAcceptanceCandidate.Create(Goals[2], 0, [], new string('b', 40), new string('a', 40));
            var calls = 0;
            var decision = solo.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, (item, _, _, _, _) =>
            {
                calls++;
                return ConductorParallelAcceptanceRunResult.Fault(item, new InvalidOperationException("fixture terminal"));
            });
            Assert.Equal(1, calls);
            Assert.True(File.Exists(decision.Attempt.MetadataPath));
            solo.MarkReconciled(decision.Attempt);
        }

        internal string Reconcile(MergeTrainReceipt receipt, string verdict)
        {
            File.WriteAllText(Attempt.ResultPath, JsonSerializer.Serialize(new
            {
                Attempt.IdentityValue, Status = "completed", Verdict = verdict, receipt.ReceiptId
            }));
            var events = new List<string>();
            var coordinator = new ConductorGroupedGateAttemptCoordinator(Root, isProcessAlive: _ => false,
                utcNow: () => Started.AddMinutes(3), eventSink: events.Add, stateDirectory: Root);
            Assert.Equal("Reconciled", coordinator.TryReconcileDead(Attempt, "child-result-published").Outcome);
            coordinator.TryReconcileDead(Attempt, "child-result-published");
            return Assert.Single(events);
        }

        internal string ExpectedLine(MergeTrainReceipt receipt, string verdict, bool originalMembers = false) =>
            $"ACCEPTANCE_COHORT_CHILD_COMPLETED kind=train attempt={Attempt.AttemptId} " +
            $"members={(originalMembers ? OriginalMembers : string.Join('+', receipt.Identity.Members.Select(member => member.GoalId.Value)))} " +
            $"identity={(originalMembers ? Original.Value : receipt.Identity.Value)} owner=4242 reason=child-result-published " +
            $"result=completed verdict={verdict} receipt={receipt.ReceiptId}";

        public void Dispose() => Directory.Delete(_worktree, recursive: true);
    }
}
