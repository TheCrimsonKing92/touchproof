using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorBatchLoop;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorAcceptanceSweepRemedy
{
    internal static ReconcileSweepRemediationCoordinator CreateCoordinator(
        OrchestratorWorkspace workspace,
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        ReconcileSweepOptions options) =>
        new(new ReconcileSweepRemediationStore(workspace.SqliteStatePath), options,
            remedy => Execute(kernel, driver, remedy, policy),
            remedy => GoalGitFactIndex.Build(workspace.ExecutionDirectory, workspace.IntegrationBranch)
                .TryGetGoalBranchTip(remedy.GoalId));

    internal static TerminalGoalRemedyExecutionResult Execute(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        TerminalGoalRemedy remedy,
        ConductorAutonomyPolicy policy)
    {
        var mutationBlockReason = driver.LandingMutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(mutationBlockReason))
        {
            return TerminalGoalRemedyExecutionResult.Retryable(
                75,
                $"landing mutation boundary unavailable: {mutationBlockReason}");
        }

        var goal = kernel.GetGoal(remedy.GoalId);
        var candidate = TryBuildParallelAcceptanceCandidate(driver, goal, policy, slotIndex: 0, out var buildException, out _);
        if (candidate is null)
        {
            return new TerminalGoalRemedyExecutionResult(
                1,
                buildException is null
                    ? $"acceptance candidate unavailable for goal {remedy.GoalPrefix}"
                    : $"acceptance candidate unavailable: {buildException.GetType().Name}: {buildException.Message}");
        }

        ConductorParallelAcceptanceAttemptDecision decision;
        try
        {
            decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                candidate,
                policy,
                driver.RunParallelLandingAcceptance);
            if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Started &&
                decision.Attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                decision = driver.ParallelAcceptanceAttemptCoordinator.Evaluate(
                    candidate,
                    policy,
                    driver.RunParallelLandingAcceptance);
            }
        }
        catch (AcceptanceArtifactWriterLeaseBusyException ex)
        {
            return TerminalGoalRemedyExecutionResult.Retryable(
                75,
                $"acceptance artifact writer busy; retry on next conduct tick: {ex.Message}");
        }

        if (decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            return TerminalGoalRemedyExecutionResult.Pending(
                $"background acceptance attempt {decision.Attempt.AttemptId} is {decision.Kind.ToString().ToLowerInvariant()}",
                decision.Attempt.AttemptId);
        }

        if (decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)
        {
            var retryable = IsRetryableTerminalAttempt(decision.Attempt);
            var output =
                $"attempt={decision.Attempt.AttemptId} outcome={AcceptanceAttemptOutcomeToken(decision.Attempt.Outcome)} " +
                $"detail={decision.Attempt.Detail ?? "no result artifact was produced"}";
            driver.ParallelAcceptanceAttemptCoordinator.MarkReconciled(decision.Attempt);
            return retryable
                ? TerminalGoalRemedyExecutionResult.Retryable(75, output)
                : new TerminalGoalRemedyExecutionResult(1, output);
        }

        var run = decision.Run ?? ConductorParallelAcceptanceRunResult.Fault(
            candidate,
            new InvalidOperationException("Completed acceptance attempt had no run result."));
        PassedAcceptanceReceiptReplay.ReconcileUnlessReused(kernel, goal, run, decision.Attempt);
        var completion = CompleteParallelAcceptanceRun(
            driver,
            policy,
            run,
            decision.Attempt,
            out var evidenceMutationLeaseHeld);
        MarkParallelAcceptanceReconciledUnlessLeaseHeld(
            driver,
            run,
            evidenceMutationLeaseHeld,
            decision.Attempt);
        var capturedOutput =
            $"attempt={decision.Attempt.AttemptId} outcome={AcceptanceRunDisposition(run)} " +
            $"detail={decision.Attempt.Detail ?? "no attempt detail was recorded"} completion={completion.Outcome}";

        if (run.Exception is DotnetBuildSlotsBusyException or OperationCanceledException ||
            (run.Exception is AcceptanceGateEngineException &&
                decision.Attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap) ||
            (run.Exception is (AcceptanceInfrastructureDeferredException or BuildLockBlockedException) &&
                decision.Attempt.TransientFailureCount < ParallelAcceptanceTransientFailureCap) ||
            IsEnvironmentInterferenceAcceptanceRun(run) ||
            IsIdentityStaleRegated(run, decision.Attempt) ||
            completion.IsHeld && run.EarlyResult is null)
        {
            return TerminalGoalRemedyExecutionResult.Retryable(75, capturedOutput);
        }

        return new TerminalGoalRemedyExecutionResult(
            completion.WasExecuted || completion.IsDone ? 0 : 1,
            capturedOutput);
    }
}
