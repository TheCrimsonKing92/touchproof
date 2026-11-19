using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: synthetic workers, goal-local state and uniquely owned repository copies. No worker is launched.
public sealed class FindingRoundBatchCompletionTests
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void StaggeredReceipts_DrainBeforeOneRoleSelection(bool sourceBlocker)
    {
        using var scenario = new Scenario(sourceBlocker);
        scenario.AssertOnlyBlocker(sourceBlocker);
        for (var tick = 0; tick < 2; tick++)
        {
            var result = scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(tick + 1, scenario.Runs.Count);
            Assert.Empty(scenario.Retries);
            Assert.DoesNotContain(scenario.Goal.Timeline, item =>
                item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal));
            Assert.Empty(scenario.Prepared);
            Assert.Equal(WorkTaskStatus.Failed, scenario.Tester.Status);
            Assert.Empty(DispatchReadinessRules.BuildReadyTaskParallelPlan(scenario.Goal).Batches);
            Assert.Empty(scenario.Prepare());
            scenario.Restart();
            scenario.AssertOnlyBlocker(sourceBlocker);
        }
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, Assert.Single(scenario.Retries));
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(3, summary.NewlyExecuted);
        Assert.Equal(0, summary.Unavailable);
        Assert.Contains(sourceBlocker ? "Developer" : "Tester", summary.RoutingReason, StringComparison.Ordinal);
        if (!sourceBlocker)
        {
            Assert.Equal(2, summary.CoalescedSelectionCount);
            Assert.Equal(2, summary.TesterCallsAvoided);
            Assert.Equal(0, summary.ReviewerCallsAvoided);
            Assert.Equal(2, summary.PaidVerificationCallsAvoided);
            Assert.All(summary.EligibilityWitnesses, witness =>
            {
                Assert.NotNull(witness.ReceiptId);
                Assert.Contains("interpretation route", witness.Eligibility!, StringComparison.Ordinal);
            });
        }
        else
        {
            Assert.Equal(2, summary.CoalescedSelectionCount);
            Assert.Equal(0, summary.TesterCallsAvoided);
            Assert.Equal(0, summary.ReviewerCallsAvoided);
            Assert.Equal(0, summary.PaidVerificationCallsAvoided);
            Assert.All(summary.EligibilityWitnesses, witness => Assert.Equal(AgentRole.Developer, witness.Role));
        }
        var prepared = Assert.Single(scenario.Prepared);
        Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, prepared.Task.Id);
        Assert.True(File.Exists(prepared.PromptPath));
        Assert.DoesNotContain(scenario.Prepared, item => item.Task.RequiredRole == AgentRole.Reviewer);
        Assert.Equal(0, scenario.ProcessStarts);
        Assert.DoesNotContain(scenario.Goal.Tasks, task => task.LastProcess is { IsRunning: true });
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredCurrentReceipts_ReconcileBeforeOneClosingSelection(bool sourceBlocker)
    {
        using var scenario = new Scenario(sourceBlocker) { RecordBatchAccounting = false };
        // Retain production receipts without batch observations, as on an upgrade or interrupted write.
        for (var request = 0; request < 3; request++)
            Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
                ConductorAutonomyPolicy.Permissive, out _));
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Equal(3, scenario.Tester.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId).Count());
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith("finding-evidence batch-", StringComparison.Ordinal));
        Assert.Empty(scenario.Retries);

        scenario.RecordBatchAccounting = true;
        scenario.Restart();
        for (var observation = 0; observation < 2; observation++)
        {
            Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
                ConductorAutonomyPolicy.Permissive, out var reconciled));
            Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, reconciled.TargetTaskId);
            Assert.NotNull(reconciled.BatchClosure);
        }
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(3, scenario.Runs.Count);
        Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, Assert.Single(scenario.Retries));
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(3, summary.NewlyExecuted);
        Assert.Equal(0, summary.Covered);
        Assert.Equal(0, summary.Unavailable);
        Assert.Contains(sourceBlocker ? "Developer" : "Tester", summary.RoutingReason, StringComparison.Ordinal);
        Assert.Equal(sourceBlocker ? scenario.Developer.Id : scenario.Tester.Id, Assert.Single(scenario.Prepared).Task.Id);
        Assert.Equal(0, scenario.ProcessStarts);

        scenario.Restart();
        Assert.Single(scenario.Retries);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Equal(summary.BatchSize, scenario.ClosingSummary().BatchSize);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TerminalFailure_SettlesUnavailableAndDrainsLaterRequests()
    {
        using var scenario = new Scenario(false, failure: "terminal");
        for (var tick = 0; tick < 3; tick++)
        {
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.Empty(scenario.Retries);
        }
        Assert.Equal(3, scenario.Runs.Count);
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(0, summary.Covered);
        Assert.Single(scenario.Escalations);
        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Single(scenario.Escalations);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void FailedEscalationDelivery_LeavesBatchReplayable()
    {
        using var scenario = new Scenario(false, failure: "terminal");
        scenario.EscalationResult = new(0, "lock-timeout", 0, "error");
        for (var tick = 0; tick < 2; tick++)
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        var failed = scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(failed.Outcome);
        Assert.Contains("escalation delivery remains pending", held.Reason);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal("Hold", decision.Action);
        Assert.Equal("finding-batch-escalation-delivery-pending", decision.DiscriminatingEvidence);
        Assert.Contains("JSON: lock-timeout, collaboration: error", decision.Reason);
        Assert.Single(scenario.Escalations);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith("finding-evidence batch-closed;"));

        scenario.Restart();
        scenario.EscalationResult = new(0, "lock-timeout", 0, "ok");
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver.AdvanceOnce(
            scenario.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
        Assert.Equal(2, scenario.Escalations.Count);
        Assert.Equal(1, scenario.ClosingSummary().Unavailable);
        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(2, scenario.Escalations.Count);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Empty(scenario.Retries);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void FailedAttemptDispositionWrite_ReplaysBeforePublishingTaskReceipt()
    {
        using var scenario = new Scenario(false);
        scenario.RefuseAttemptDispositionWrites = true;
        Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
            ConductorAutonomyPolicy.Permissive, out var failed));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidenceReceiptPersistenceFailed, failed.Kind);
        Assert.True(scenario.RefusedDispositionWrites > 0);
        Assert.Empty(scenario.Tester.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
        Assert.Null(failed.BatchClosure);

        scenario.Restart();
        scenario.RefuseAttemptDispositionWrites = false;
        Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
            ConductorAutonomyPolicy.Permissive, out var replayed));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, replayed.Kind);
        Assert.Single(scenario.Runs);
        Assert.Single(scenario.Tester.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId));
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void UnavailableAdvisory_PreservesQualifiedClosingRetry(bool sourceBlocker)
    {
        using var scenario = new Scenario(sourceBlocker, failure: "terminal-advisory");
        for (var tick = 0; tick < 2; tick++)
        {
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.Empty(scenario.Prepared);
            Assert.Empty(scenario.Retries);
            Assert.Empty(scenario.Escalations);
        }
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(3, scenario.Runs.Count);
        var target = sourceBlocker ? scenario.Developer : scenario.Tester;
        Assert.Equal(target.Id, Assert.Single(scenario.Retries));
        Assert.Equal(target.Id, Assert.Single(scenario.Prepared).Task.Id);
        Assert.Empty(scenario.Escalations);
        Assert.Equal(0, scenario.ProcessStarts);
        var summary = scenario.ClosingSummary();
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Contains(target.RequiredRole.ToString(), summary.RoutingReason, StringComparison.Ordinal);
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void RefusedMember_SettlesUnavailableBeforeBatchCloses(bool previouslyRefused)
    {
        using var scenario = new Scenario(false, failure: previouslyRefused ? "prior-refusal" : "normalization");
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(scenario.Tester.LastVerification, out _, out _));
        if (previouslyRefused)
            Assert.Contains(scenario.Tester.LastVerification!.MergedReviewFindings!, finding =>
                finding.StableId == "first" && finding.EvidenceOutcome?.Reason == FindingEvidenceNotHonouredReason.UnparseableSelection);
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(scenario.Runs);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Escalations);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal));
        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(1, summary.Unavailable);
        Assert.Contains("FindingOperatorEvidenceRequired", summary.RoutingReason, StringComparison.Ordinal);
        Assert.Single(scenario.Escalations);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [InlineData("rejected")]
    [InlineData("run-failed")]
    [Trait("Category", "CrossTick")]
    public void TransientFailure_RecoversWithinAllowanceAfterRestart(string failure)
    {
        using var scenario = new Scenario(false, failure);
        scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(scenario.Runs);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Escalations);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        Assert.Contains(scenario.Tester.LastVerification!.MergedReviewFindings!, finding => finding.EvidenceOutcome?.Reason ==
            (failure == "rejected" ? FindingEvidenceNotHonouredReason.SelectionApparatusFailure : FindingEvidenceNotHonouredReason.RunFailed));
        Assert.Equal(0, FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "pending").Unavailable);
        scenario.Restart();
        for (var tick = 0; tick < 3; tick++)
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(4, scenario.Runs.Count);
        Assert.Equal(scenario.Runs[0], scenario.Runs[1]);
        Assert.Equal(scenario.Tester.Id, Assert.Single(scenario.Retries));
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.NewlyExecuted);
        Assert.Equal(0, summary.Unavailable);
        Assert.Empty(scenario.Escalations);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void ExhaustedTransientFailure_DrainsLaterMembersWithoutRetry()
    {
        using var scenario = new Scenario(false, failure: "always-rejected");
        for (var tick = 0; tick < 5; tick++)
        {
            scenario.Driver.AdvanceOnce(scenario.Goal, ConductorAutonomyPolicy.Permissive);
            Assert.Empty(scenario.Retries);
        }
        Assert.Equal(5, scenario.Runs.Count);
        Assert.Equal(3, scenario.Runs.Count(request => request == scenario.Runs[0]));
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(1, summary.Unavailable);
        Assert.Single(scenario.Escalations);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData("launch-failed", nameof(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed))]
    [InlineData("process-died", nameof(ConductorParallelAcceptanceAttemptOutcome.ProcessDied))]
    [InlineData("corrupt-artifacts", nameof(ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts))]
    public void RepeatedAttemptFailure_UsesRunAllowanceAndDrainsLaterRequests(string failure, string expectedOutcome)
    {
        using var scenario = new Scenario(false, failure);
        var policy = ConductorAutonomyPolicy.Permissive;
        scenario.CapacityBlocked = true;

        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        Assert.Empty(scenario.FailedAttemptRequests);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);

        scenario.CapacityBlocked = false;
        var attemptIds = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 1; attempt <= FindingRoundBatchSchedule.MaximumCompletedRuns; attempt++)
        {
            scenario.AdvanceEvidenceClock();
            Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
            if (failure != "launch-failed")
            {
                var running = scenario.LatestAttempt();
                Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, running.Outcome);
                if (failure == "corrupt-artifacts")
                    File.WriteAllText(running.ExitCodePath, "1");
                scenario.AdvanceEvidenceClock();
                Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
            }
            var terminal = scenario.LatestAttempt();
            Assert.Equal(expectedOutcome, terminal.Outcome.ToString());
            Assert.NotNull(terminal.ReconciledAt);
            Assert.True(attemptIds.Add(terminal.AttemptId));
            Assert.Equal(attempt, scenario.FailedAttemptRequests.Count);
            Assert.Single(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
                item.Message.Contains($"attempt_id={terminal.AttemptId};", StringComparison.Ordinal));
            Assert.Empty(scenario.Retries);
            Assert.Empty(scenario.Prepared);
        }
        Assert.All(scenario.FailedAttemptRequests, request => Assert.Contains("AlphaTests", request));
        var failures = scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded).ToArray();
        Assert.Equal(FindingRoundBatchSchedule.MaximumCompletedRuns, failures.Length);
        Assert.All(failures, item => Assert.Contains("reason=run-failed;", item.Message, StringComparison.Ordinal));
        Assert.Empty(scenario.Runs);
        Assert.Empty(scenario.Escalations);

        scenario.LaunchRecovered = true;
        scenario.Restart();
        scenario.AdvanceEvidenceClock();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        Assert.Contains("BetaTests", Assert.Single(scenario.Runs));
        Assert.Empty(scenario.Retries);
        scenario.AdvanceEvidenceClock();
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        Assert.Equal(2, scenario.Runs.Count);
        Assert.Contains("GammaTests", scenario.Runs[1]);
        var summary = scenario.ClosingSummary();
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Single(scenario.Escalations);
        Assert.Empty(scenario.Retries);
        Assert.Equal(0, scenario.ProcessStarts);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task FailedRunChargeWrite_ReplaysAndChargesTheAttemptOnce()
    {
        using var scenario = new Scenario(false, failure: "launch-failed");
        var policy = ConductorAutonomyPolicy.Permissive;
        var beforeFailure = scenario.Kernel.ExportSnapshot();
        scenario.RefuseRunChargeCheckpoint = true;

        var failure = Assert.Throws<IOException>(() =>
            scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester, policy, out _));
        Assert.Equal("Injected failure checkpointing the finding evidence run.", failure.Message);
        var attempt = scenario.LatestAttempt();
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.LaunchFailed, attempt.Outcome);
        Assert.Null(attempt.ReconciledAt);
        Assert.DoesNotContain(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);

        scenario.RefuseRunChargeCheckpoint = false;
        scenario.Restore(AgentOrchestratorKernel.FromSnapshot(beforeFailure));
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        Assert.Equal(identity.RoundFingerprint, attempt.FindingRoundFingerprint);
        var finding = scenario.Findings[0];
        Assert.True(ConductorDriver.TryNormalizeFindingEvidenceRequest(finding.EvidenceRequest!,
            scenario.Driver._getFindingEvidenceEngineSettings(scenario.Goal), (_, _) => [],
            out var request, out var executorRequest, out _, out _));
        var group = new FindingEvidenceRequestGroup(ConductorDriver.BuildFindingEvidenceIdentity(request), executorRequest, request, [finding]);
        var batch = Assert.Single(FindingEvidenceRoundPlanner.BuildFindingEvidenceBatches([group]));
        Assert.Equal(attempt.FocusedEvidenceRequest, batch.Request);

        using var barrier = new Barrier(2);
        Task<FailedGoalFindingObservation> Replay() => Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Both charge observers did not reach the barrier.");
            return FindingRoundBatchCoordinator.ObserveUnfinishedExecution(
                scenario.Driver, scenario.Goal, scenario.Tester, batch, true, attempt.BranchHeadSha!,
                attempt.FocusedExecutionBasisIdentity!, identity, ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun,
                attempt, FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending, "Launch failed"), null);
        });
        Assert.All(await Task.WhenAll(Replay(), Replay()), observation =>
            Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observation.Kind));
        Assert.Equal(attempt.AttemptId, scenario.LatestAttempt().AttemptId);
        Assert.NotNull(scenario.LatestAttempt().ReconciledAt);
        Assert.Single(scenario.FailedAttemptRequests);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);

        scenario.Restore(AgentOrchestratorKernel.FromSnapshot(Assert.IsType<OrchestratorSnapshot>(scenario.LastRunChargeCheckpoint)));
        var charge = Assert.Single(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);
        Assert.Contains($"attempt_id={attempt.AttemptId};", charge.Message, StringComparison.Ordinal);
        scenario.AdvanceEvidenceClock();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        Assert.NotEqual(attempt.AttemptId, scenario.LatestAttempt().AttemptId);
        Assert.Equal(2, scenario.Goal.Timeline.Count(item => item.Kind == ProgressKind.FindingEvidenceRunRecorded));
        Assert.All(scenario.FailedAttemptRequests, requestText => Assert.Contains("AlphaTests", requestText));
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void PermanentRefusal_ReplaysFromRunChargeWithoutRelaunch()
    {
        using var scenario = new Scenario(false, failure: "unsupported-first");
        var policy = ConductorAutonomyPolicy.Permissive;
        scenario.Driver.AdvanceOnce(scenario.Goal, policy);
        var refusedRequest = Assert.Single(scenario.Runs);
        Assert.Contains("AlphaTests", refusedRequest, StringComparison.Ordinal);
        var attempt = scenario.LatestAttempt();
        Assert.NotNull(attempt.ReconciledAt);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
        Assert.Empty(scenario.Escalations);

        // The attempt metadata was reconciled after this snapshot was persisted.
        scenario.Restore(AgentOrchestratorKernel.FromSnapshot(Assert.IsType<OrchestratorSnapshot>(scenario.LastRunChargeCheckpoint)));
        var charge = Assert.Single(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded);
        Assert.Contains($"attempt_id={attempt.AttemptId};", charge.Message, StringComparison.Ordinal);
        Assert.Contains("reason=unsupported-project;", charge.Message, StringComparison.Ordinal);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        Assert.DoesNotContain(FindingRoundBatchAccounting.Read(scenario.Goal, identity), item => item.Kind == "unavailable");

        scenario.Driver.AdvanceOnce(scenario.Goal, policy);
        Assert.Equal(2, scenario.Runs.Count);
        Assert.NotEqual(refusedRequest, scenario.Runs[1]);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
        Assert.Empty(scenario.Escalations);
        var unavailable = Assert.Single(FindingRoundBatchAccounting.Read(scenario.Goal, identity), item => item.Kind == "unavailable");
        Assert.Equal("first", Assert.Single(unavailable.FindingIds));
        Assert.Contains("unsupported-project", unavailable.Eligibility, StringComparison.Ordinal);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, scenario.Tester.LastVerification!.MergedReviewFindings!
            .Single(finding => finding.StableId == "first").EvidenceOutcome?.Reason);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, policy);
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Single(scenario.Runs, request => request == refusedRequest);
        Assert.Single(scenario.Escalations);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
        Assert.Equal(0, scenario.ProcessStarts);
        var summary = scenario.ClosingSummary();
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(2, summary.NewlyExecuted);
        Assert.Equal(1, summary.Unavailable);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CoalescedPermanentRefusal_ReplaysAllMembersAfterPartialWrite()
    {
        using var scenario = new Scenario(false, failure: "unsupported-batch");
        var policy = ConductorAutonomyPolicy.Permissive;
        FindingEvidenceOutcome? Outcome(string id) =>
            scenario.Tester.LastVerification!.MergedReviewFindings!.Single(finding => finding.StableId == id).EvidenceOutcome;
        scenario.RefuseFindingOutcome = "second";
        var failure = Assert.Throws<IOException>(() => scenario.Driver.AdvanceOnce(scenario.Goal, policy));
        Assert.Equal("Injected failure writing the finding evidence outcome.", failure.Message);
        var refusedRequest = Assert.Single(scenario.Runs);
        Assert.Contains("AlphaTests", refusedRequest, StringComparison.Ordinal);
        Assert.Contains("BetaTests", refusedRequest, StringComparison.Ordinal);
        var attempt = scenario.LatestAttempt();
        Assert.Null(attempt.ReconciledAt);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, Outcome("first")?.Reason);
        Assert.Null(Outcome("second"));
        Assert.Empty(scenario.Retries);

        scenario.RefuseFindingOutcome = null;
        scenario.Restart();
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(scenario.Driver.AdvanceOnce(scenario.Goal, policy).Outcome);
        Assert.Equal(2, scenario.Runs.Count);
        Assert.Contains("GammaTests", scenario.Runs[1], StringComparison.Ordinal);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, Outcome("second")?.Reason);
        var unavailable = FindingRoundBatchAccounting.Read(scenario.Goal, identity).Where(item => item.Kind == "unavailable").ToArray();
        Assert.Equal(new[] { "first", "second" }, unavailable.SelectMany(item => item.FindingIds).Order(StringComparer.Ordinal));
        var refusalReceipt = Assert.Single(unavailable.Select(item => item.ReceiptId).Distinct());
        Assert.NotNull(refusalReceipt);
        var charge = Assert.Single(scenario.Goal.Timeline, item => item.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            item.Message.StartsWith(FindingRoundBatchCoordinator.FailedAttemptRunPrefix, StringComparison.Ordinal));
        Assert.Contains($"receipt_id={refusalReceipt};", charge.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts([scenario.Goal.Id.Value]),
            item => item.AttemptId == attempt.AttemptId);

        scenario.Restart();
        scenario.Driver.AdvanceOnce(scenario.Goal, policy);
        Assert.Equal(2, scenario.Runs.Count);
        Assert.Single(scenario.Escalations);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Prepared);
        Assert.Equal(0, scenario.ProcessStarts);
        Assert.Equal(2, scenario.ClosingSummary().Unavailable);
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task ConcurrentClose_AdmitsOneRetryAndSurvivesSnapshot()
    {
        using var scenario = new Scenario(true);
        scenario.AssertOnlyBlocker(sourceBlocker: true);
        FailedGoalFindingObservation observation = FailedGoalFindingObservation.None;
        for (var index = 0; index < 3; index++)
        {
            Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
                ConductorAutonomyPolicy.Permissive, out observation));
            if (index < 2)
            {
                Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observation.Kind);
                Assert.Null(observation.TargetTaskId);
                Assert.Null(observation.BatchClosure);
            }
            scenario.AssertOnlyBlocker(sourceBlocker: true);
        }
        Assert.Equal(3, scenario.Runs.Count);
        Assert.Empty(scenario.Retries);
        Assert.Empty(scenario.Escalations);
        var closure = Assert.IsType<FindingEvidenceBatchClosure>(observation.BatchClosure);
        Assert.Equal(scenario.Developer.Id, observation.TargetTaskId);
        using var timeline = scenario.Goal.Timeline.GetEnumerator();
        Assert.True(timeline.MoveNext());
        using var barrier = new Barrier(2);
        var applied = 0;
        async Task<bool> Close() => await Task.Run(() =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Both batch close attempts did not reach the admission barrier.");
            return FindingRoundBatchCoordinator.TryApply(scenario.Driver, scenario.Goal, closure, () => true, () =>
            {
                Interlocked.Increment(ref applied);
                // Keep the requesting round readable: duplicate protection must come from admission,
                // rather than accidentally passing because the first retry erased its verification.
                scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Developer.Id, "Batch drained; repair source.", invalidateDownstream: false);
                return true;
            });
        });
        var results = await Task.WhenAll(Close(), Close());
        while (timeline.MoveNext())
            Assert.NotEqual(ProgressKind.TaskRetried, timeline.Current.Kind);
        Assert.Single(results.Where(result => result));
        Assert.Equal(1, applied);
        Assert.Single(scenario.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
            ConductorAutonomyPolicy.Permissive, out var observedClosure));
        Assert.Equal(FailedGoalFindingObservationKind.FindingEvidencePending, observedClosure.Kind);
        Assert.Null(observedClosure.TargetTaskId);
        scenario.Restart();
        Assert.False(FindingEvidenceBatchClosureAdmission.TryApply(scenario.Goal, closure, () => true,
            () => throw new InvalidOperationException("A closed batch retried after restoration.")));
        Assert.Equal(3, scenario.ClosingSummary().BatchSize);
    }

    [Fact]
    public void FailedSnapshotWrite_RestoresTheOpenRoundAndCommitsRetryWithAccounting()
    {
        using var scenario = new Scenario(false);
        FindingEvidenceBatchClosure Drain()
        {
            FailedGoalFindingObservation observation;
            do
            {
                Assert.True(scenario.Driver.TryBuildFindingEvidenceRequest(scenario.Goal, scenario.Tester,
                    ConductorAutonomyPolicy.Permissive, out observation));
            } while (observation.BatchClosure is null && scenario.Runs.Count < 3);
            Assert.Equal(scenario.Tester.Id, observation.TargetTaskId);
            return Assert.IsType<FindingEvidenceBatchClosure>(observation.BatchClosure);
        }
        var closure = Drain();
        var durable = scenario.Kernel.ExportSnapshot();
        bool Apply(bool fail) => FindingRoundBatchCoordinator.TryApply(scenario.Driver, scenario.Goal, closure, () => true, () =>
        {
            scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Tester.Id, "Interpret the completed batch.");
            Assert.Null(scenario.Tester.LastVerification);
            var candidate = scenario.Kernel.ExportSnapshot();
            var submitted = AgentOrchestratorKernel.FromSnapshot(candidate).GetGoal(scenario.Goal.Id);
            Assert.True(FindingRoundBatchAccounting.IsClosed(submitted, closure.Identity));
            Assert.Single(submitted.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
            if (fail) throw new IOException("Injected goal snapshot write failure");
            durable = candidate;
            return true;
        });

        Assert.Throws<IOException>(() => Apply(fail: true));
        scenario.Restore(AgentOrchestratorKernel.FromSnapshot(durable));
        Assert.NotNull(scenario.Tester.LastVerification);
        Assert.False(FindingRoundBatchAccounting.IsClosed(scenario.Goal, closure.Identity));
        closure = Drain();
        Assert.True(Apply(fail: false));
        scenario.Restore(AgentOrchestratorKernel.FromSnapshot(durable));
        Assert.Null(scenario.Tester.LastVerification);
        Assert.Equal(3, scenario.ClosingSummary().NewlyExecuted);
        Assert.Equal(3, scenario.Runs.Count);
    }

    [Fact]
    public void Accounting_DistinctMembershipAndOutcomesSurviveReplay()
    {
        using var scenario = new Scenario(false);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        var duplicate = scenario.Findings[0] with { StableId = "same-request" };
        FindingRoundBatchAccounting.Begin(scenario.Goal, identity, [.. scenario.Findings, duplicate], scenario.Record);
        var members = FindingRoundBatchAccounting.Read(scenario.Goal, identity).Where(item => item.Kind == "member").ToArray();
        Assert.Equal(3, members.Length);
        for (var replay = 0; replay < 2; replay++)
        {
            for (var index = 0; index < members.Length; index++)
                FindingRoundBatchAccounting.Record(scenario.Goal, identity,
                    members[index] with { Kind = new[] { "executed", "covered", "unavailable" }[index], ReceiptId = "receipt-" + index }, scenario.Record);
            scenario.Restart();
        }
        var summary = FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "evidence-unavailable");
        Assert.Equal(3, summary.BatchSize);
        Assert.Equal(1, summary.NewlyExecuted);
        Assert.Equal(1, summary.Covered);
        Assert.Equal(1, summary.Unavailable);
        Assert.Equal(0, summary.CoalescedSelectionCount);
        Assert.Equal(0, summary.PaidVerificationCallsAvoided);
    }

    [Fact]
    public void ChangedIdentity_DoesNotInheritAccountingOrClose()
    {
        using var scenario = new Scenario(false);
        var identity = FindingRoundBatchCoordinator.Identity(scenario.Driver, scenario.Goal, scenario.Tester)!;
        FindingRoundBatchAccounting.Begin(scenario.Goal, identity, scenario.Findings, scenario.Record);
        FindingRoundBatchAccounting.Record(scenario.Goal, identity,
            new(identity.Key, "suppressed-selection", "receipt:tester", [], "receipt", null, "unknown counterfactual"), scenario.Record);
        FindingRoundBatchAccounting.Record(scenario.Goal, identity,
            new(identity.Key, "suppressed-selection", "receipt:tester", [], "receipt", null, "unknown counterfactual"), scenario.Record);
        var summary = FindingRoundBatchAccounting.Summarize(scenario.Goal, scenario.Tester, identity, "pending");
        Assert.Equal(1, summary.UnknownSelections);
        Assert.Null(summary.PaidVerificationCallsAvoided);
        Assert.Equal(0, summary.CoalescedSelectionCount);
        foreach (var changed in new[]
        {
            identity with { RequestingTaskId = scenario.Developer.Id }, identity with { CandidateSha = "def5678" },
            identity with { RoundFingerprint = "new-round" }, identity with { ExecutionBasisIdentity = "new-basis" }
        })
        {
            Assert.Empty(FindingRoundBatchAccounting.Read(scenario.Goal, changed));
            Assert.False(FindingRoundBatchCoordinator.TryApply(scenario.Driver, scenario.Goal, new(changed, "changed"),
                () => true, () => throw new InvalidOperationException("A changed batch inherited closure.")));
        }
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = SeededGitRepositoryTemplate.CreateCopy();
        private readonly string Candidate;
        private readonly string? _failure;
        internal Scenario(bool sourceBlocker, string? failure = null)
        {
            Assert.True(GitRevisionReader.Shared.TryResolve(_root, "HEAD", out var candidate));
            Candidate = candidate;
            _failure = failure;
            Kernel = new AgentOrchestratorKernel();
            Goal = Kernel.CreateGoal("Drain finding evidence before verification", [
                new TaskSpec(TaskId.New(), "Implement source", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Verify source", AgentRole.Tester),
                new TaskSpec(TaskId.New(), "Review source", AgentRole.Reviewer)]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            PassVerification(Kernel, Goal, Developer, hasCommittedChanges: true);
            Kernel.RecordDispatchResultCommit(Goal.Id, Developer.Id, Candidate);
            Findings = [
                EvidenceFindingWithRequest("First evidence request requires interpretation", "first",
                    FindingCategory.TestCoverage, classes: ["AlphaTests"]) with
                    { Severity = sourceBlocker ? FindingSeverity.Advisory : FindingSeverity.Blocking },
                EvidenceFindingWithRequest("Second request", "second", project: failure == "unsupported-batch" ? "Infrastructure.Tests" : "Core.Tests",
                    classes: ["BetaTests"]) with { Severity = FindingSeverity.Advisory },
                EvidenceFindingWithRequest("Third request", "third", classes: ["FullyQualifiedName~GammaTests.Case&Category!=Slow"]) with { Severity = FindingSeverity.Advisory }];
            if (sourceBlocker)
                Findings = [.. Findings, EvidenceFindingWithRequest("Source repair remains required", "source-blocker",
                    FindingCategory.Correctness) with { EvidenceRequest = null }];
            if (failure == "normalization")
                Findings[0] = Findings[0] with { EvidenceRequest = new([new("Unknown.Tests", "MissingTests")]) };
            DispatchTask(Kernel, Goal, Tester, "test", workingDirectory: _root, baseCommit: Candidate);
            var stdout = string.Join(Environment.NewLine, "WORKER_RESULT:", "files: none", "commands: inspect",
                "tests: deferred - acceptance owns execution", "commit: none", "blockers: none",
                $"findings: {JsonSerializer.Serialize(Findings)}", "touched_anchors: []", "verdict: needs-work",
                "model_fit: fixture/model - adequate - source verification", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new("test", _root, 0, stdout, "",
                DateTimeOffset.UtcNow, StandardOutputPath: Path.Combine(_root, "tester.log"), WorkerResultPresent: true));
            if (failure == "prior-refusal")
                Kernel.RecordFindingEvidenceOutcome(Goal.Id, Tester.Id, Findings[0].StableId,
                    new(false, Reason: FindingEvidenceNotHonouredReason.UnparseableSelection));
            Driver = CreateDriver();
        }
        internal AgentOrchestratorKernel Kernel { get; private set; }
        internal Goal Goal { get; private set; }
        internal TaskSpec Developer => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        internal TaskSpec Tester => Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        internal ReviewFinding[] Findings { get; }
        internal ConductorDriver Driver { get; private set; }
        internal List<string> Runs { get; } = [];
        internal List<string> FailedAttemptRequests { get; } = [];
        internal bool CapacityBlocked { get; set; }
        internal bool LaunchRecovered { get; set; }
        internal bool RefuseRunChargeCheckpoint { get; set; }
        internal string? RefuseFindingOutcome { get; set; }
        internal OrchestratorSnapshot? LastRunChargeCheckpoint { get; private set; }
        private bool FailsOwnedAttempts => _failure is "launch-failed" or "process-died" or "corrupt-artifacts";
        private DateTimeOffset _evidenceNow = DateTimeOffset.UtcNow;
        internal void AdvanceEvidenceClock() => _evidenceNow = _evidenceNow.AddSeconds(2);
        internal ConductorParallelAcceptanceAttempt LatestAttempt() =>
            Assert.IsType<ConductorParallelAcceptanceAttempt>(Driver.FocusedEvidenceAttemptCoordinator.TryReadLatest(Goal.Id.Value));
        internal List<TaskId> Retries { get; } = [];
        internal List<string> Escalations { get; } = [];
        internal LandingEscalationWriteResult EscalationResult { get; set; } = LandingEscalationWriteResult.CompletedWithoutExternalSinks;
        internal bool RefuseAttemptDispositionWrites { get; set; }
        internal int RefusedDispositionWrites { get; private set; }
        internal List<WorkerProfileDispatchResult> Prepared { get; } = [];
        internal int ProcessStarts { get; private set; }
        internal bool RecordBatchAccounting { get; set; } = true;
        internal void AssertOnlyBlocker(bool sourceBlocker)
        {
            var blocker = Assert.Single(ReviewFindings.GetOpenBlockingFindings(Tester.LastVerification!.MergedReviewFindings!,
                Goal.EffectiveAcceptanceCriteriaCorrections));
            Assert.Equal(sourceBlocker ? "source-blocker" : "first", blocker.StableId);
            if (sourceBlocker)
            {
                Assert.Equal(FindingCategory.Correctness, blocker.Category);
                Assert.Null(blocker.EvidenceRequest);
            }
        }
        internal void Record(GoalId goal, TaskId task, string message)
        {
            if (RecordBatchAccounting || !message.StartsWith("finding-evidence batch-", StringComparison.Ordinal))
                Kernel.RecordFindingEvidenceRequest(goal, task, message);
        }
        internal void Restart() => Restore(AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot()));

        internal void Restore(AgentOrchestratorKernel restored)
        {
            var id = Goal.Id;
            Kernel = restored;
            Goal = Kernel.GetGoal(id);
            Driver = CreateDriver();
        }
        internal IReadOnlyList<WorkerProfileDispatchResult> Prepare()
        {
            var ready = DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(Goal, DefaultAgents());
            return Goal.Tasks.Where(task => ready.TaskIds.Contains(task.Id)).Select(task => WorkerProfileDispatcher.PrepareTask(
                Kernel, Goal, task, new WorkerProfile("fixture", "worker --prompt {promptPath}"), Path.Combine(_root, "prompts"),
                _root, DateTimeOffset.UtcNow, sandboxOptions: new WorkerSandboxOptions(false, "fixture", "fixture"))).ToArray();
        }
        private ConductorDriver CreateDriver() => MakeDriver(
            executionDirectory: _root,
            getLandingFileScopes: _ => [],
            getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(_root,
                TrunkBranchName.Default, runInline: !FailsOwnedAttempts || LaunchRecovered,
                utcNow: FailsOwnedAttempts ? () => _evidenceNow : null,
                isProcessAlive: FailsOwnedAttempts ? _ => false : null,
                recentHeartbeatGrace: TimeSpan.FromSeconds(1),
                launchOwnedProcess: FailsOwnedAttempts ? launch =>
                {
                    FailedAttemptRequests.Add(launch.Attempt.FocusedEvidenceRequest!);
                    if (_failure == "launch-failed")
                        throw new InvalidOperationException("Injected owned-process launch failure");
                    return new(12345);
                } : null,
                readPreReviewEvidenceCapacity: FailsOwnedAttempts ? _ =>
                    CapacityBlocked ? new(0, 2, 0, true) : new(2, 2, 0, true) : null,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(Path.Combine(_root, "conduct.jsonl"),
                    beforeRequiredEventDrain: RejectDispositionWrite)),
            runFocusedEvidence: (_, request) =>
            {
                Runs.Add(request);
                if ((_failure is "unsupported-first" or "unsupported-batch") && request == Runs[0])
                    return new(request, false, false, "Project unavailable", [],
                        Rejection: new(FocusedEvidenceRejectionCode.UnsupportedProject, request, "Project unavailable"));
                if (_failure == "terminal" && Runs.Count == 1 || _failure == "terminal-advisory" && Runs.Count == 2)
                    return new(request, false, false, "Request unavailable", []);
                if ((_failure == "rejected" && Runs.Count == 1) ||
                    (_failure == "always-rejected" && request == Runs[0]))
                    return new(request, false, false, "Source discovery unavailable", [],
                        Rejection: new(FocusedEvidenceRejectionCode.SourceDiscoveryFailure, request, "Source discovery unavailable"));
                if (_failure == "run-failed" && Runs.Count == 1)
                    throw new InvalidOperationException("Focused executor failed before producing evidence.");
                // Passing requests can settle independently of a source finding that has no evidence request.
                var green = ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate);
                return green with { Arms = green.Arms!.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray(), OutcomeReason = FindingEvidenceOutcomeReason.ValidEvidence };
            },
            retryTaskWithRoundKind: (goal, task, message, round) =>
            {
                Retries.Add(task);
                return Kernel.RetryTask(goal, task, message, retryRoundKind: round);
            },
            dispatchAndStart: _ =>
            {
                Prepared.AddRange(Prepare());
                return DispatchStartOutcome.Deferred("Preparation only; no process launcher configured.");
            },
            startRecordedDispatches: _ => { ProcessStarts++; throw new InvalidOperationException("Paid worker start prohibited."); },
            writeEscalationWithResult: (_, _, message) =>
            {
                Escalations.Add(message);
                return EscalationResult;
            },
            recordFindingEvidenceRequest: Record,
            recordFindingEvidenceRun: RecordRun,
            recordFindingEvidenceRequestDispositions: (goal, task, dispositions) => Kernel.RecordFindingEvidenceRequestDispositions(goal, task, dispositions),
            recordFindingEvidenceOutcome: (goal, task, id, outcome, receipt) =>
            {
                if (id == RefuseFindingOutcome)
                    throw new IOException("Injected failure writing the finding evidence outcome.");
                Kernel.RecordFindingEvidenceOutcome(goal, task, id, outcome, receipt);
            });

        private void RecordRun(GoalId goal, TaskId task, string message)
        {
            if (!message.StartsWith(FindingRoundBatchCoordinator.FailedAttemptRunPrefix, StringComparison.Ordinal))
            {
                Kernel.RecordFindingEvidenceRun(goal, task, message);
                return;
            }
            FindingRoundBatchAccounting.CheckpointRun(Kernel.GetGoal(goal), task, message, _evidenceNow, () =>
            {
                if (RefuseRunChargeCheckpoint)
                    throw new IOException("Injected failure checkpointing the finding evidence run.");
                Assert.Null(LatestAttempt().ReconciledAt);
                LastRunChargeCheckpoint = Kernel.ExportSnapshot();
            }, _ => { });
        }

        private void RejectDispositionWrite()
        {
            var directory = Path.Combine(_root, Goal.Id.Value);
            if (!RefuseAttemptDispositionWrites || !Directory.Exists(directory) ||
                !Directory.EnumerateFiles(directory, "*.attempt.json").Any(path =>
                    File.ReadAllText(path).Contains("\"focusedEvidenceReceiptId\": \"finding-evidence-", StringComparison.OrdinalIgnoreCase))) return;
            RefusedDispositionWrites++;
            throw new IOException("Injected failure writing the required disposition event.");
        }
        internal FindingRoundBatchAccounting.Summary ClosingSummary()
        {
            var item = Assert.Single(Goal.Timeline.Where(item => item.Message.StartsWith("finding-evidence batch-closed;", StringComparison.Ordinal)));
            return JsonSerializer.Deserialize<FindingRoundBatchAccounting.Summary>(item.Message[(item.Message.IndexOf('{'))..])!;
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
