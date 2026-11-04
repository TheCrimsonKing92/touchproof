using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private OrchestratorBuildEvidenceResolution ResolveWorkerBuildInvocation(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, IReadOnlyList<string> changedPaths, IReadOnlyList<string> dirtyPaths,
        string workerOutput, bool completeWorkerResult, Func<OrchestratorBuildEvidenceResolution> reconcileBuild,
        ref string? orchestratorFailureReason)
    {
        if (WorkerBuildInvocations is null)
        {
            // Missing apparatus must deny verification without inventing a native worker exit.
            orchestratorFailureReason ??= "worker-build-consumer-unavailable";
            return OrchestratorBuildEvidenceCheck.RetainUnverifiedBusyClaim(
                new(true, false, "worker_build_host=unavailable; enrolled dispatch cannot use legacy worker receipts"), workerOutput);
        }
        var receipt = WorkerBuildInvocations.Consume(kernel, goalId, taskId, changedPaths, dirtyPaths);
        var host = new OrchestratorBuildEvidenceResolution(receipt.MissingEvidence, receipt.CompilerFailed, receipt.Diagnostic);
        if (receipt.Gap == WorkerBuildEvidenceGap.Rejected)
        {
            orchestratorFailureReason ??= "worker-build-authority-rejected";
            return host with { Diagnostic = AppendDiagnostic(host.Diagnostic,
                "completion_build_hold=authority-rejected; operator decision required before build execution") };
        }
        var task = kernel.GetTask(goalId, taskId);
        var infrastructure = WorkerBuildInfrastructureEvidence.Classify(task);
        var missingOutcome = receipt.Gap == WorkerBuildEvidenceGap.MissingApplicableInvocation &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester && completeWorkerResult &&
            WorkerResultBlockers.TryGetAssignedScopeComplete(workerOutput, out var scopeComplete, out _) && scopeComplete;
        if (infrastructure == WorkerBuildInfrastructureBlocker.None && !missingOutcome)
            return OrchestratorBuildEvidenceCheck.RetainUnverifiedBusyClaim(host, workerOutput);
        if (!receipt.MissingEvidence || receipt.CompilerFailed ||
            WorkerResultBlockers.TryGetAssignedScopeComplete(workerOutput, out var complete, out _) && !complete)
            return host;
        if (_completionBuildEvidence is null)
        {
            orchestratorFailureReason ??= "completion-build-coordinator-unavailable";
            return host with { Diagnostic = AppendDiagnostic(host.Diagnostic,
                "completion_build_hold=coordinator-unavailable; operator decision required to restore existing build execution authority") };
        }

        var build = reconcileBuild();
        var diagnostic = missingOutcome
            ? "worker_build_recovery=missing-applicable-outcome; transport_cause=unknown; automatic_attempt_limit=1"
            : $"worker_build_infrastructure={infrastructure}";
        if (build.Pending || build.BuildFailed ||
            !build.FailsRound && build.Operation is { Phase: CompletionBuildEvidencePhase.Success } operation &&
            operation.DispatchAt == task.LastDispatch?.DispatchedAt)
            return build with { Diagnostic = AppendDiagnostic(build.Diagnostic, diagnostic) };

        // A receipt alone cannot certify a protocol-1 round, nor can another dispatch's operation.
        return host with { Diagnostic = AppendDiagnostic(AppendDiagnostic(host.Diagnostic, build.Diagnostic), diagnostic) };
    }
}
