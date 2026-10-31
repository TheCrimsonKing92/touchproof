using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its kernels, stores and attempt directories. The collection isolates
// the conductor's gate census; no worker or acceptance process is launched.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsPassedGateHoldFaultIsolation(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outcome_reconcile_failure_logs_and_preserves_hold_tracking(bool storeFailure)
    {
        var (kernel, goal) = SimpleGoal();
        var resolves = 0;
        var store = new FakeCollaborationItemStore
        {
            ResolveOverride = _ =>
            {
                resolves++;
                if (storeFailure && resolves == 1) throw new IOException("controlled store failure");
                return Task.FromResult(false);
            }
        };
        var driver = MakeDriver();
        var reads = 0;
        driver.PassedGateHoldAttention = new(store, _ =>
        {
            reads++;
            if (!storeFailure && reads == 1) throw new IOException("controlled candidate failure");
            return null;
        });
        var changed = new HashSet<GoalId>();
        var lines = new List<string>();
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "held landing");

        var errors = AsyncLocalConsoleRouter.CaptureError(() => ConductorBatchLoop.TrackGoalOutcome(
            kernel, driver, goal, held, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(10), changed, lines));

        Assert.Equal(1, reads);
        Assert.Equal(storeFailure ? 1 : 0, resolves);
        Assert.Contains($"PASSED_GATE_HOLD_MAINTENANCE_FAILED goal={goal.Id.Value} operation=reconcile-outcome", errors);
        Assert.Contains("exception=IOException", errors);
        Assert.Contains(storeFailure ? "controlled store failure" : "controlled candidate failure", errors);
        Assert.Equal("held landing", goal.CurrentHold?.Blocker);
        Assert.NotNull(goal.LandingBlocker);
        Assert.Contains(goal.Id, changed);

        ConductorBatchLoop.TrackGoalOutcome(kernel, driver, goal,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "resolved"),
            DateTimeOffset.UnixEpoch.AddMinutes(1), TimeSpan.FromMinutes(10), changed, lines);
        Assert.Equal(2, reads);
        Assert.Null(goal.CurrentHold);
        Assert.Null(goal.LandingBlocker);
    }

    [Fact]
    public void Attention_sweep_logs_one_goal_failure_and_reconciles_the_next()
    {
        var kernel = new AgentOrchestratorKernel();
        var bad = HeldGoal(kernel);
        var good = HeldGoal(kernel);
        var store = new FakeCollaborationItemStore();
        var reads = new List<GoalId>();
        var attention = new PassedGateHoldAttention(store, goal =>
        {
            reads.Add(goal.Id);
            if (goal.Id == bad.Id) throw new IOException("controlled candidate failure");
            return new(PassedGateHoldCategory.CriterionEvidenceObligation, "criterion 1", "missing evidence",
                "passed-attempt", Candidate, ["criterion-evidence-record"]);
        });

        var errors = AsyncLocalConsoleRouter.CaptureError(() => attention.ReconcileAll([bad, good]));

        Assert.Equal(new[] { bad.Id, good.Id }, reads);
        Assert.Contains($"goal={bad.Id.Value} operation=reconcile-attention", errors);
        Assert.Contains("exception=IOException", errors);
        Assert.Equal(good.Id.Value, Assert.Single(store.Items).GoalId);
        Assert.NotNull(bad.LandingBlocker);
    }

    [Theory]
    [InlineData("refresh-held-gate-receipts")]
    [InlineData("reconcile-passed-gate-holds")]
    public void Prewalk_phase_failure_logs_and_allows_a_healthy_goal_to_advance(string phase)
    {
        var (kernel, goal) = SimpleGoal();
        var workspaceCalls = new List<GoalId>();
        var driver = MakeDriver(createWorkspace: current =>
        {
            workspaceCalls.Add(current.Id);
            return "workspace ready";
        });
        var injectedFailures = 0;
        var text = AsyncLocalConsoleRouter.Capture(() =>
        {
            var summary = new ConductorBatchLoop(janitorialPhaseProbe: current =>
            {
                if (current != phase) return;
                injectedFailures++;
                throw new IOException("controlled maintenance failure");
            }).Run(kernel, driver, ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);
            Assert.Equal(1, summary.Ticks);
        });

        Assert.Equal(1, injectedFailures);
        Assert.Equal(goal.Id, Assert.Single(workspaceCalls));
        Assert.Contains("LOOP_JANITORIAL_FAILED tick=1 phase=" + phase, text);
        Assert.Contains("exception=IOException", text);
        Assert.DoesNotContain("reason=unintended-exit", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_sweep_logs_candidate_failure_and_retains_other_pending_goals(bool invalidOperation)
    {
        var root = CreateTempDirectory("mcg-held-receipt-fault");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var bad = HeldGoal(kernel);
            var good = HeldGoal(kernel);
            WriteReceipt(root, bad);
            WriteReceipt(root, good, pending: true);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default);
            var reads = new List<GoalId>();
            IReadOnlyList<string>? pending = null;

            var errors = AsyncLocalConsoleRouter.CaptureError(() =>
                pending = PassedReceiptManifestInvalidation.CheckAll([bad, good], coordinator, root, goal =>
                {
                    reads.Add(goal.Id);
                    throw invalidOperation
                        ? new InvalidOperationException("controlled candidate failure")
                        : new IOException("controlled candidate failure");
                }));

            Assert.Equal(bad.Id, Assert.Single(reads));
            Assert.Equal(good.Id.Value, Assert.Single(pending!));
            Assert.Contains($"goal={bad.Id.Value} operation=refresh-held-receipt", errors);
            Assert.Contains("exception=" + (invalidOperation ? "InvalidOperationException" : "IOException"), errors);
            Assert.Contains("controlled candidate failure", errors);
            Assert.True(coordinator.HasPassedReconciledReceipt(bad.Id.Value));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Journal_failure_after_invalidation_logs_and_keeps_the_regate_pending()
    {
        var root = CreateTempDirectory("mcg-held-receipt-journal-fault");
        try
        {
            var goal = HeldGoal(new AgentOrchestratorKernel());
            WriteReceipt(root, goal);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
                { "version": 1, "checks": [{ "name": "check", "type": "command", "command": "git", "arguments": ["diff", "--check"] }] }
                """);
            Assert.NotEqual(coordinator.TryReadLatest(goal.Id.Value)!.EffectiveManifestIdentity,
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(root, ["docs/workflow.md"]));
            // A directory at the exact journal file path deterministically rejects append.
            var journalPath = GoalOperationJournal.PathFor(root, goal.Id);
            Directory.CreateDirectory(journalPath);
            Assert.True(Directory.Exists(journalPath));
            Assert.True(coordinator.HasPassedReconciledReceipt(goal.Id.Value));

            var errors = AsyncLocalConsoleRouter.CaptureError(() =>
                PassedReceiptManifestInvalidation.CheckAll([goal], coordinator, root, _ => Candidate));

            Assert.Contains($"goal={goal.Id.Value} operation=refresh-held-receipt", errors);
            Assert.NotNull(coordinator.TryReadLatest(goal.Id.Value)!.RegateRequestedAt);
            Assert.False(coordinator.HasPassedReconciledReceipt(goal.Id.Value));
            var pending = PassedReceiptManifestInvalidation.CheckAll([goal], coordinator, root,
                _ => throw new InvalidOperationException("Pending invalidation must not compare again."));
            Assert.Equal(goal.Id.Value, Assert.Single(pending));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Goal HeldGoal(AgentOrchestratorKernel kernel)
    {
        var goal = CreateVerifiedSimpleGoal(kernel, "held landing");
        goal.ObserveLandingBlocker(new("landing/criterion-evidence-rebind-outstanding",
            "criterion 1 missing evidence", "hold", DateTimeOffset.UnixEpoch, "operator", "criterion-evidence-record"));
        return goal;
    }

    private static void WriteReceipt(string root, Goal goal, bool pending = false)
    {
        var directory = Path.Combine(root, goal.Id.Value);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "passed-attempt.attempt.json");
        var receipt = new ConductorParallelAcceptanceAttempt(
            "passed-attempt", goal.Id.Value, goal.Id.Value[..8], 0, Candidate, new string('b', 40),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0,
            ConductorParallelAcceptanceAttemptOutcome.Passed, "", "", "", "", "passed.result.json", path,
            ReconciledAt: DateTimeOffset.UnixEpoch, ExecutionProtocol: "in-process")
        {
            EffectiveManifestIdentity = "effective-manifest-sha256-old",
            RegateRequestedAt = pending ? DateTimeOffset.UnixEpoch : null,
            ScopePaths = ["docs/workflow.md"]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(receipt));
    }
}
