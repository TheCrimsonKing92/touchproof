using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

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

    private protected void AssertLongCohortGateLandsBothGoals(int iteration = 1, Action? whileGateHeld = null)
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
            var firstGoal = CreateCompletedGoal(kernel, "First production cohort member", repo);
            var secondGoal = CreateCompletedGoal(kernel, "Second production cohort member", repo);
            _ = CreateWorktreeCandidate(
                repo,
                firstGoal.Id,
                "src/Mcg.AgentOrchestrator.Infrastructure/First.cs",
                "first");
            _ = CreateWorktreeCandidate(repo, secondGoal.Id, "tests/Second.cs", "second");
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

                    Assert.True(gateStarted.Wait(TimeSpan.FromSeconds(10)), "The controlled cohort verifier did not start.");
                    intentStore.EnqueueAsync(new OperatorIntentRecord(
                        intentId,
                        "cohort-running-progress-key",
                        OperatorIntentVerbs.Progress,
                        firstGoal.Id.Value,
                        firstGoal.Tasks.Single().Id.Value,
                        JsonSerializer.Serialize(
                            new ProgressOperatorIntentPayload(WorkTaskStatus.Completed, "operator intent applied while cohort gate remained held"),
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
            Assert.Equal(2, gateProgressEvents.Length);
            Assert.Contains(gateProgressEvents, record => record.GoalId == firstGoal.Id.Value[..8]);
            Assert.Contains(gateProgressEvents, record => record.GoalId == secondGoal.Id.Value[..8]);
            Assert.All(gateProgressEvents, record =>
            {
                Assert.Contains($"members={firstGoal.Id.Value[..8]},{secondGoal.Id.Value[..8]}", record.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("goal=unknown", record.Detail, StringComparison.Ordinal);
            });
            var cohortEvents = conductEvents
                .Where(record => record.EventKind == "acceptance-cohort")
                .ToArray();
            Assert.StartsWith("ACCEPTANCE_COHORT_ENTRY", cohortEvents[0].Detail, StringComparison.Ordinal);
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
            Assert.Contains(cohortEvents, record => record.Detail.StartsWith("ACCEPTANCE_COHORT_EXIT", StringComparison.Ordinal));
            var fairnessTransition = Assert.Single(cohortEvents.Where(record =>
                record.Detail.StartsWith("ACCEPTANCE_COHORT_FAIRNESS_TRANSITION", StringComparison.Ordinal)));
            Assert.Contains($"oldest={firstGoal.Id.Value}", fairnessTransition.Detail, StringComparison.Ordinal);
            Assert.Contains("previous=1 resulting=0 oldest_admitted=True", fairnessTransition.Detail, StringComparison.Ordinal);
            var inFlightTicks = cohortEvents
                .Where(record => record.Detail.StartsWith("ACCEPTANCE_COHORT_INFLIGHT", StringComparison.Ordinal))
                .Select(record => record.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Single(token => token.StartsWith("tick=", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(inFlightTicks.Length >= 2, "Expected in-flight cohort records from at least two completed ticks.");

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
            Assert.True(landable, $"Iteration {iteration}: cohort did not become landable: {readiness.Detail}");
            Assert.NotNull(backgroundGate);
            Assert.True(backgroundGate.Wait(TimeSpan.FromSeconds(10)), "The background cohort gate did not complete.");
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
            AssertNoCohortWorkspaces(repo);
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*), MIN(gate_exit_code), MAX(json_array_length(gate_test_result_paths_json))
                FROM cohort_receipts;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(0, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));

            (bool Landable, string Detail) ObserveLandingReadiness()
            {
                using var receiptConnection = new SqliteConnection(
                    $"Data Source={Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False");
                receiptConnection.Open();
                using var receiptCommand = receiptConnection.CreateCommand();
                receiptCommand.CommandText =
                    "SELECT COUNT(*), MIN(outcome), MIN(valid_for_landing) FROM cohort_receipts;";
                using var receiptReader = receiptCommand.ExecuteReader();
                Assert.True(receiptReader.Read());
                var count = receiptReader.GetInt32(0);
                var outcome = receiptReader.IsDBNull(1) ? "missing" : receiptReader.GetString(1);
                var valid = !receiptReader.IsDBNull(2) && receiptReader.GetInt32(2) == 1;
                var roots = driver.GetActiveAcceptanceCohortCapacity().ActiveRoots;
                var membersActive = roots.Any(root =>
                    root.MemberGoalIds.Contains(firstGoal.Id.Value) || root.MemberGoalIds.Contains(secondGoal.Id.Value));
                var detail = $"receipt_count={count} outcome={outcome} valid_for_landing={valid} " +
                    $"members_active={membersActive} active_roots=[{string.Join(';', roots.Select(root =>
                        $"{root.Key}:{string.Join(',', root.MemberGoalIds.Order(StringComparer.Ordinal))}"))}]";
                return (count == 1 && outcome == nameof(AcceptanceCohortGateOutcome.Passed) && valid && !membersActive, detail);
            }
        }
        finally
        {
            gateRelease.Set();
            teardown = RemoveAfterObservedDrain(
                () => driver is null || driver.GetActiveAcceptanceCohortCapacity().ActiveRootCount == 0,
                "long-cohort background run completed before fixture removal",
                TimeSpan.FromSeconds(30),
                () => { if (File.Exists(trx)) File.Delete(trx); },
                () => DeleteDirectory(repo));
        }
        teardown.AssertDrained();
    }
}
