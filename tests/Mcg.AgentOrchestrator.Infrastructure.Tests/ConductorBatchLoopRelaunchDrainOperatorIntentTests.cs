using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique SQLite/artifact roots, injected clock and process records; no children.
public sealed class ConductorBatchLoopRelaunchDrainOperatorIntentTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Xunit.Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    [Xunit.Trait("Category", "CrossTick")]
    public void HoldingWorker_RoutingIntent_IsAppliedBeforeRebuild(bool dispatchHandlingChanged)
    {
        var result = RunScenario(dispatchHandlingChanged);

        // Without the drain boundary, this observation is Pending until the handoff.
        Assert.Equal(OperatorIntentStatus.Applied, result.StatusDuringDrain);
        Assert.False(result.RebuiltDuringObservation);
        Assert.Equal(WorkTaskStatus.Assigned, result.TargetStatus);
        Assert.Equal(WorkTaskStatus.Assigned, result.DurableTargetStatus);
        var firstDrain = result.Output.IndexOf("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal);
        var applied = result.Output.IndexOf("OPERATOR_INTENT id=during-drain ", StringComparison.Ordinal);
        var lastDrain = result.Output.LastIndexOf("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal);
        var rebuild = result.Output.IndexOf("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal);
        Assert.True(firstDrain >= 0 && applied > firstDrain && lastDrain > applied && rebuild > lastDrain,
            "Expected drain, intent application, another drain, then rebuild in that order.");
        Assert.Contains("result=applied-pending-commit", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, result.AppliedMarkers);
        if (dispatchHandlingChanged)
            Assert.Contains("reason=dispatch-handling-changed", result.Output, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void DrainAndHandoff_SkipDispatchGatesAndLaterIntent()
    {
        var result = RunScenario(dispatchHandlingChanged: true);

        Assert.Equal(1, result.Handoffs);
        Assert.True(result.DrainPolls >= 2, "The running-worker drain was not observed twice.");
        Assert.True(result.WorkQueuedDuringDrain, "The dispatch and gate controls must be eligible.");
        Assert.True(result.RebuildObservedAtHandoff, "The late intent must be queued after rebuild.");
        Assert.Equal(0, result.WorkerStartsDuringDrain);
        Assert.Equal(0, result.GateAdmissionsDuringDrain);
        Assert.Equal(OperatorIntentStatus.Pending, result.LateIntentStatus);
        Assert.Equal(WorkTaskStatus.Failed, result.LateTargetStatus);
        Assert.DoesNotContain("OPERATOR_INTENT id=after-rebuild ", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("LOOP_RELAUNCH_DETACH", result.Output, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Xunit.Trait("Category", "CrossTick")]
    public void ExpiredAdmission_DuringDrain_IsRejectedOnExistingDeadline()
    {
        var result = RunScenario(dispatchHandlingChanged: true, expireIntent: true);

        Assert.Equal(OperatorIntentStatus.Rejected, result.StatusDuringDrain);
        Assert.False(result.RebuiltDuringObservation);
        Assert.Equal(WorkTaskStatus.Failed, result.TargetStatus);
        Assert.Contains("task-intent-admission-expired", result.IntentOutcome!, StringComparison.Ordinal);
        Assert.Equal(0, result.AppliedMarkers);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, false, false, false)]
    [Xunit.InlineData(false, false, true, false)]
    [Xunit.InlineData(false, true, false, false)]
    [Xunit.InlineData(false, true, true, true)]
    [Xunit.InlineData(true, false, false, false)]
    [Xunit.InlineData(true, false, true, false)]
    [Xunit.InlineData(true, true, false, false)]
    [Xunit.InlineData(true, true, true, false)]
    public void DrainDecision_OnlyUnservicedWorkerHold_RunsBoundary(
        bool ranAtTickStart, bool heldForRunningDispatches, bool stillPending, bool expected)
    {
        // Count/canary failures, a null outcome, and gates-only holds carry held=false.
        Assert.Equal(expected, ConductorRelaunchDrainIntentBoundary.RunsAfterDrainStep(
            ranAtTickStart, new(ConductorRelaunchDrainIntentBoundary.SelfRelaunchDrainDisposition.ContinueTick,
                HeldForRunningDispatches: heldForRunningDispatches), stillPending));
    }

    [Xunit.Fact]
    public void DrainDecision_MissingOrRebuildingOutcome_SkipsBoundary()
    {
        Assert.False(ConductorRelaunchDrainIntentBoundary.RunsAfterDrainStep(false, null, true));
        var rebuilding = new ConductorRelaunchDrainIntentBoundary.SelfRelaunchDrainOutcome(
            ConductorRelaunchDrainIntentBoundary.SelfRelaunchDrainDisposition.BreakLoop,
            HeldForRunningDispatches: true);
        Assert.False(ConductorRelaunchDrainIntentBoundary.RunsAfterDrainStep(false, rebuilding, true));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    public void TickStartDecision_PendingRelaunch_SkipsBoundary(bool pending, bool expected) =>
        Assert.Equal(expected, ConductorRelaunchDrainIntentBoundary.RunsAtTickStart(pending));

    [Xunit.Fact]
    public void DrainOutcome_Fault_PropagatesOriginalException()
    {
        var fault = new InvalidOperationException("handoff authority was not confirmed");
        var outcome = new ConductorRelaunchDrainIntentBoundary.SelfRelaunchDrainOutcome(
            ConductorRelaunchDrainIntentBoundary.SelfRelaunchDrainDisposition.Proceed, fault);
        Assert.Same(fault, Assert.Throws<InvalidOperationException>(outcome.ThrowIfFaulted));
    }

    private static ScenarioResult RunScenario(bool dispatchHandlingChanged, bool expireIntent = false)
    {
        var root = CreateTempDirectory("relaunch-drain-intents");
        try
        {
            var now = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
            var eventPath = Path.Combine(root, "events.log");
            var kernel = new AgentOrchestratorKernel();
            var landing = CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
            var running = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Running worker");
            var runningTask = running.Tasks.Single();
            const string command = "worker Developer";
            kernel.RecordTaskDispatch(running.Id, runningTask.Id,
                new TaskDispatchRecord("test-worker", command, root, now, BaseCommit: "abc123"));
            kernel.RecordTaskProcessStarted(running.Id, runningTask.Id, new TaskProcessRecord(
                111, command, root, Path.Combine(root, "out"), Path.Combine(root, "err"),
                Path.Combine(root, "exit"), now, null, null,
                OwnedProcessIds: [111], ProcessIdentityStartedAt: now));
            var target = FailedGoal("Drain routing target");
            var lateTarget = FailedGoal("Post-handoff routing target");
            var statePath = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(statePath);
            var repository = new SqliteOrchestratorStateRepository(statePath);
            repository.SaveAsync(kernel).GetAwaiter().GetResult();
            var store = new SqliteOperatorIntentStore(Path.Combine(root, "intents.db"),
                Path.Combine(root, "wake"), utcNow: () => now);
            var landed = false;
            var starts = 0;
            var admissions = 0;
            var startsAtDrain = 0;
            var admissionsAtDrain = 0;
            var handoffs = 0;
            var drainPolls = 0;
            var sleeps = 0;
            var workQueued = false;
            var rebuildAtHandoff = false;
            OperatorIntentStatus? observedStatus = null;
            var rebuiltAtObservation = false;
            var driver = MakeDriver(
                getFacts: goal => goal.Id == landing.Id && landed
                    ? new GoalLifecycleFacts(WorkspaceExists: true, IsMerged: true, IsRecorded: true, IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
                runAcceptance: _ => { admissions++; return true; },
                runAcceptanceWithSlot: (_, _) => { admissions++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
                runAcceptanceCohort: (_, _, _) =>
                {
                    admissions++;
                    throw new InvalidOperationException("Cohort admitted during drain.");
                },
                runMergeTrain: (_, _, _) =>
                {
                    admissions++;
                    throw new InvalidOperationException("Merge train admitted during drain.");
                },
                land: goal =>
                {
                    landed = true;
                    return new LandingResult(goal.Id.Value, goal.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed");
                },
                getLandingFileScopes: _ => dispatchHandlingChanged
                    ? ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs",
                       "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultParser.cs"]
                    : ["src/Mcg.AgentOrchestrator.App/Orchestration/ConductorBatchLoop.cs"],
                mergeTrainsEnabled: false);
            var output = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop(
                utcNow: () => now,
                readRelaunchDrainCap: () => null,
                conductEventLogWriter: new ConductEventLogWriter(eventPath, utcNow: () => now),
                operatorIntents: new OperatorIntentCoordinator(store, utcNow: () => now),
                countLiveGroupedGates: () => 0,
                selfRelaunchEnabled: true,
                selfRelaunch: _ =>
                {
                    handoffs++;
                    rebuildAtHandoff = File.ReadAllText(eventPath).Contains("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal);
                    store.EnqueueAsync(Retry(lateTarget, "after-rebuild", now)).GetAwaiter().GetResult();
                    return ConductorLiveGroupedGateCountTests.Handoff();
                }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 10, watchInterval: TimeSpan.FromSeconds(1), keepAliveWhenIdle: true,
                    persistGoalTick: (current, ids) => repository.SaveGoalSnapshotsAsync(
                        ids.Select(current.ExportGoalSnapshot).ToArray()).GetAwaiter().GetResult(),
                    sleepFunc: _ =>
                    {
                        var events = File.ReadAllText(eventPath);
                        if (events.Contains("LOOP_RELAUNCH_DRAIN", StringComparison.Ordinal))
                        {
                            drainPolls++;
                            if (drainPolls == 1)
                            {
                                startsAtDrain = starts;
                                admissionsAtDrain = admissions;
                                store.EnqueueAsync(Retry(target, "during-drain", now)).GetAwaiter().GetResult();
                                var queued = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Queued worker");
                                var gate = CreateVerifiedSimpleGoal(kernel, "Queued acceptance");
                                workQueued = queued.Tasks.Single().Status == WorkTaskStatus.Assigned &&
                                    gate.Tasks.Single().Status == WorkTaskStatus.Completed;
                            }
                            if (drainPolls == 2)
                            {
                                observedStatus = store.GetAsync("during-drain").GetAwaiter().GetResult()!.Status;
                                rebuiltAtObservation = events.Contains("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal);
                            }
                            if (drainPolls == (dispatchHandlingChanged ? 5 : 2))
                                CompleteDispatchedTask(kernel, running, runningTask, now, "def456");
                            now += TimeSpan.FromMinutes(expireIntent && drainPolls == 1 ? 6 : 1);
                        }
                        if (++sleeps >= 20)
                            File.WriteAllText(Path.Combine(root, "stop"), "Running-worker drain did not reach handoff.");
                        return false;
                    }));
            var durableTarget = repository.LoadGoalsAsync([target.Id]).GetAwaiter().GetResult().GetGoal(target.Id);
            return new(output, observedStatus, rebuiltAtObservation,
                kernel.GetGoal(target.Id).Tasks.Single().Status, durableTarget.Tasks.Single().Status,
                kernel.GetGoal(target.Id).Timeline.Count(item => item.OperatorIntentApplied?.IntentId == "during-drain"),
                store.GetAsync("during-drain").GetAwaiter().GetResult()?.Outcome,
                handoffs, drainPolls, workQueued, rebuildAtHandoff, starts - startsAtDrain, admissions - admissionsAtDrain,
                store.GetAsync("after-rebuild").GetAwaiter().GetResult()?.Status,
                kernel.GetGoal(lateTarget.Id).Tasks.Single().Status);

            Goal FailedGoal(string objective)
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
                kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Failed, "operator repair");
                return goal;
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    private static OperatorIntentRecord Retry(Goal goal, string id, DateTimeOffset now) =>
        new(id, id, OperatorIntentVerbs.Retry, goal.Id.Value, goal.Tasks.Single().Id.Value,
            JsonSerializer.Serialize(new RetryOperatorIntentPayload(
                "operator repair", null, RetryCause: RetryCause.ContractClarification),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [], "operator", "test", "local-process", now);

    private sealed record ScenarioResult(
        string Output, OperatorIntentStatus? StatusDuringDrain, bool RebuiltDuringObservation,
        WorkTaskStatus TargetStatus, WorkTaskStatus DurableTargetStatus, int AppliedMarkers, string? IntentOutcome,
        int Handoffs, int DrainPolls, bool WorkQueuedDuringDrain, bool RebuildObservedAtHandoff,
        int WorkerStartsDuringDrain, int GateAdmissionsDuringDrain, OperatorIntentStatus? LateIntentStatus,
        WorkTaskStatus LateTargetStatus);
}
