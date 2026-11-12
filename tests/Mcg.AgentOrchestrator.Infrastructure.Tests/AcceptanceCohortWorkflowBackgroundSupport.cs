using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class AcceptanceCohortWorkflowBackgroundSupport : AcceptanceCohortWorkflowTests
{
    internal readonly record struct DrainedTeardown(bool Drained, string Condition)
    {
        public void AssertDrained() =>
            Assert.True(Drained, $"Background cohort gate did not drain: {Condition}.");
    }

    internal static DrainedTeardown RemoveAfterObservedDrain(
        Func<bool> drainCondition, string conditionName, TimeSpan bound, params Action[] removals)
    {
        var drained = SpinWait.SpinUntil(drainCondition, bound);
        if (drained)
        {
            foreach (var removal in removals)
                removal();
        }
        return new DrainedTeardown(drained, conditionName);
    }

    private protected void AssertLongTrainGateLandsBothGoals(int iteration = 1, Action? whileGateHeld = null)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var trx = Path.Combine(Path.GetTempPath(), $"cohort-production-{Guid.NewGuid():N}.trx");
        using var gateStarted = new ManualResetEventSlim();
        using var gateRelease = new ManualResetEventSlim();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        ConductorDriver? driver = null;
        DrainedTeardown teardown;
        try
        {
            AddAcceptanceManifest(repo);
            File.WriteAllText(trx, ValidPassingTrx());
            var kernel = new AgentOrchestratorKernel();
            var firstGoal = CreateCompletedGoal(kernel, "First production train member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production train member", repo);
            var firstRevision = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            var secondRevision = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
            foreach (var (goal, revision) in new[] { (firstGoal, firstRevision), (secondGoal, secondRevision) })
            {
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The merge train full gate passes for this member"], VerificationClass.TestVerifiable, [], []));
                kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, CriterionEvidenceOwner.Acceptance,
                    "test", CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: revision);
            }
            var verifier = new BlockingAcceptanceVerifier(
                gateStarted,
                gateRelease,
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("shared-production-gate", true, 0, null)],
                    TestResultPaths: [trx]));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            driver = new ConductorDriver(
                kernel,
                workspace,
                verifier,
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                cleanupHooks: cleanupContext.Hooks);
            Task? backgroundGate = null;
            driver.SetCohortGateBackgroundStartForTests(action => backgroundGate = Task.Run(action));
            var fairnessStore = new CohortAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"));
            fairnessStore.RecordOvertake(firstGoal.Id);
            fairnessStore.RecordOvertake(secondGoal.Id);
            var trainStore = new MergeTrainAcceptanceStore(
                Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            var conductLogPath = Path.Combine(workspace.OrchestratorDirectory, "logs", "cohort-nonblocking.jsonl");
            var intentStore = new SqliteOperatorIntentStore(
                Path.Combine(workspace.OrchestratorDirectory, "operator-intents.db"),
                Path.Combine(workspace.OrchestratorDirectory, "logs"));
            const string intentId = "cohort-running-progress";
            var ticks = new List<BatchTickSummary>();

            AdmissionFactsTestData.Warm(driver, kernel);
            var heldSummary = new ConductorBatchLoop(
                operatorIntents: new OperatorIntentCoordinator(intentStore),
                conductEventLogWriter: new ConductEventLogWriter(conductLogPath)).Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 3,
                watchInterval: TimeSpan.FromMilliseconds(1),
                sleepFunc: _ => false,
                onTick: tick =>
                {
                    ticks.Add(tick);
                    if (ticks.Count != 1)
                    {
                        return;
                    }

                    Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled train verifier did not start.");
                    intentStore.EnqueueAsync(new OperatorIntentRecord(
                        intentId,
                        "cohort-running-progress-key",
                        OperatorIntentVerbs.Progress,
                        firstGoal.Id.Value,
                        firstGoal.Tasks.Single().Id.Value,
                        JsonSerializer.Serialize(
                            new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "operator intent applied while train gate remained held"),
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        [],
                        "operator",
                        "test",
                        "local-process",
                        DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                },
                persistGoalTick: (_, _) => { });

            Assert.Equal(3, ticks.Count);
            Assert.Equal(1, verifier.RunCount);
            Assert.True(verifier.StableSlotLeaseObserved);
            Assert.True(Directory.Exists(verifier.WorktreePath));
            Assert.Equal(OperatorIntentStatus.Applied, intentStore.GetAsync(intentId).GetAwaiter().GetResult()!.Status);
            Assert.Equal(GoalStatus.Verified, firstGoal.Status);
            Assert.Equal(GoalStatus.Verified, secondGoal.Status);
            Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                [firstGoal.Id.Value, secondGoal.Id.Value]));
            Assert.Equal(0, heldSummary.Advanced);
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(firstGoal.Id));
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(secondGoal.Id));
            Assert.Contains(ticks[0].ProgressLines!, line =>
                line.StartsWith("ACCEPTANCE_TRAIN ", StringComparison.Ordinal) &&
                line.Contains($"members={firstGoal.Id.Value[..8]},{secondGoal.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("outcome=inflight", StringComparison.Ordinal));
            var conductEvents = File.ReadAllLines(conductLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToArray();
            var gateProgressEvents = File.ReadAllLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(
                    line,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "gate-progress")
                .ToArray();
            // Train admission and the controlled verifier each report progress for both members.
            Assert.Equal(4, gateProgressEvents.Length);
            foreach (var goal in new[] { firstGoal, secondGoal })
            {
                Assert.Collection(gateProgressEvents.Where(record => record.GoalId == goal.Id.Value[..8]),
                    record => Assert.Contains(" phase=execution-admitted ", record.Detail, StringComparison.Ordinal),
                    record => Assert.Contains(" phase=controlled-cohort ", record.Detail, StringComparison.Ordinal));
            }
            Assert.All(gateProgressEvents, record =>
            {
                Assert.Contains($"members={firstGoal.Id.Value[..8]},{secondGoal.Id.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.Contains("scope=merge-train", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
            var cohortEvents = conductEvents
                .Where(record => record.EventKind == "acceptance-cohort")
                .ToArray();
            Assert.DoesNotContain(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
            var inFlightTicks = cohortEvents
                .Where(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_INFLIGHT", StringComparison.Ordinal))
                .Select(record => record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Single(token => token.StartsWith("tick=", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(inFlightTicks.Length >= 2, "Expected in-flight train records from at least two completed ticks.");

            whileGateHeld?.Invoke();
            gateRelease.Set();
            // Saving the receipt precedes workspace disposal and run completion. The
            // next tick excludes both members until their background root is terminal.
            var readiness = ObserveLandingReadiness();
            var landable = SpinWait.SpinUntil(() =>
            {
                readiness = ObserveLandingReadiness();
                return readiness.Landable;
            }, TimeSpan.FromSeconds(30));
            Assert.True(landable, $"Iteration {iteration}: train did not become landable: {readiness.Detail}");
            Assert.NotNull(backgroundGate);
            Assert.True(backgroundGate.Wait(TimeSpan.FromSeconds(10)), "The background train gate did not complete.");
            Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
            BatchTickSummary? landingTick = null;
            var landedSummary = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => landingTick = tick);

            Assert.Equal(0, fairnessStore.ReadOvertakeCount(firstGoal.Id));
            Assert.Equal(0, fairnessStore.ReadOvertakeCount(secondGoal.Id));
            Assert.True(landedSummary.Advanced == 2,
                $"Iteration {iteration}: expected 2 landed goals, advanced={landedSummary.Advanced}; " +
                $"before tick: {readiness.Detail}; after tick: {ObserveLandingReadiness().Detail}; " +
                $"landing tick: {string.Join(Environment.NewLine, landingTick?.ProgressLines ?? [])}");
            Assert.True(File.Exists(Path.Combine(
                repo,
                "src",
                "Mcg.AgentOrchestrator.Infrastructure",
                "First.cs")));
            Assert.True(File.Exists(Path.Combine(repo, "tests", "Second.cs")));
            AssertNoMergeTrainWorkspaces(repo);
            AssertNoCohortWorkspaces(repo);
            Assert.Equal(1, verifier.RunCount);
            var receipt = Assert.Single(trainStore.ReadReceiptsSince(DateTimeOffset.MinValue));
            Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
            Assert.Equal(0, receipt.GateExitCode);
            Assert.Single(receipt.GateTestResultPaths);
            Assert.Equal(new[] { firstGoal.Id, secondGoal.Id }, receipt.Identity.Members.Select(member => member.GoalId));
            Assert.Contains(receipt.Identity.Value, trainStore.ReadFinalizedLandingTrainIds());

            (bool Landable, string Detail) ObserveLandingReadiness()
            {
                var receipts = trainStore.ReadReceiptsSince(DateTimeOffset.MinValue);
                var count = receipts.Count;
                var outcome = receipts.FirstOrDefault()?.Outcome.ToString() ?? "missing";
                var valid = receipts.FirstOrDefault()?.ValidForLanding == true;
                var roots = driver.GetActiveAcceptanceCohortCapacity().ActiveRoots;
                var membersActive = roots.Any(root =>
                    root.MemberGoalIds.Contains(firstGoal.Id.Value) || root.MemberGoalIds.Contains(secondGoal.Id.Value));
                var detail = $"receipt_count={count} outcome={outcome} valid_for_landing={valid} " +
                    $"members_active={membersActive} active_roots=[{string.Join(';', roots.Select(root =>
                        $"{root.Key}:{string.Join(',', root.MemberGoalIds.Order(StringComparer.Ordinal))}"))}]";
                return (count == 1 && outcome == nameof(MergeTrainGateOutcome.Passed) && valid && !membersActive, detail);
            }
        }
        finally
        {
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => driver is null || driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                "long-train background run completed before fixture removal",
                TimeSpan.FromSeconds(30),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }
}
