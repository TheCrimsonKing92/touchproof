using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every scenario owns its repository, storage roots and coordinator.
public sealed class AcceptanceCohortWorkflowTestsProductionBatchReconciliation : AcceptanceCohortWorkflowBackgroundSupport
{
    [Fact]
    public void ProductionBatch_LongCohortGateDoesNotBlockTicksOrOperatorIntents_AndReconcilesLater()
        => AssertLongTrainGateLandsBothGoals();

    [Fact]
    public void ProductionBatch_OrdinaryParallelAcceptanceStillStartsPromptlyAndReconcilesLater()
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var attemptRoot = Path.Combine(repo, ".orchestrator", "ordinary-negative-control");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Ordinary acceptance negative control", repo);
            ConductorParallelAcceptanceOwnedProcessLaunch? launch = null;
            const int ownerProcessId = 8123;
            var landed = false;
            var holders = new AcceptanceHolderExitFixture();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => true,
                classifyHolder: holders.Classify,
                resultPublishedForTests: holders.RecordExit,
                launchOwnedProcess: pending =>
                {
                    launch = pending;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(ownerProcessId);
                },
                acquireStableSlotLease: (_, _) => null);
            var driver = new ConductorDriver(
                getFacts: _ => landed
                    ? new GoalLifecycleFacts(
                        WorkspaceExists: true,
                        IsMerged: true,
                        IsRecorded: true,
                        IsCleanedUp: true)
                    : new GoalLifecycleFacts(WorkspaceExists: true),
                getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => repo,
                dispatchAndStart: _ => DispatchStartOutcome.Started(),
                startRecordedDispatches: null,
                buildServerShutdown: null,
                runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                runAdvisorySemanticAcceptance: null,
                retryTask: null,
                recordTaskNote: null,
                recordCriterionRetryFeedback: null,
                clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                    GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                    GoalWorktrees.BranchName(goal.Id),
                    "current",
                    [],
                    null),
                land: (candidate, _) =>
                {
                    landed = true;
                    return new LandingResult(
                        candidate.Id.Value,
                        candidate.Id.Value[..8],
                        new LandingDecision.Promote(),
                        LandingExecutor.IntegrationBranchName,
                        MainAdvanced: true,
                        "landed");
                },
                afterSuccessfulLanding: null,
                record: _ => { },
                cleanup: _ => new GoalWorktreeRemoveResult("clean", null, [], null),
                writeEscalation: (_, _, _) => { },
                classifyChangeRisk: _ => ChangeRiskTier.Behavior,
                getLandingFileScopes: _ => ["src/Ordinary.cs"],
                runAcceptanceVerificationWithSlot: (_, _) =>
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                parallelAcceptanceAttemptCoordinator: coordinator);
            var stopPath = Path.Combine(repo, "stop-does-not-exist");
            BatchTickSummary? startTick = null;

            var started = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => startTick = tick);

            Assert.Equal(0, started.Advanced);
            Assert.Equal(1, started.Held);
            Assert.Equal(GoalStatus.Verifying, goal.Status);
            Assert.NotNull(launch);
            Assert.False(driver.TryGetCohortGateHold(goal.Id, out _));
            Assert.Contains(startTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            launch.ExecuteInCurrentProcess(ownerProcessId);

            BatchTickSummary? reconcileTick = null;
            var completed = new ConductorBatchLoop().Run(
                kernel,
                driver,
                ConductorAutonomyPolicy.Permissive,
                stopPath,
                maxIterations: 1,
                onTick: tick => reconcileTick = tick);

            Assert.Equal(1, completed.Advanced);
            Assert.True(landed);
            Assert.Contains(reconcileTick!.ProgressLines!, line =>
                line.Contains("ACCEPTANCE", StringComparison.Ordinal) &&
                line.Contains("result=passed", StringComparison.Ordinal));
            Assert.Empty(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }
}
