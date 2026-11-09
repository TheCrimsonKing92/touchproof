using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class CompletionBuildEvidenceCoordinator
{
    internal CompletionBuildEvidenceOperation RequestRelaunchRecovery(AgentOrchestratorKernel kernel,
        GoalId goalId, TaskId taskId, CompletionBuildEvidenceTarget target)
    {
        var task = kernel.GetTask(goalId, taskId);
        var original = task.CompletionBuildEvidence
            ?? throw new InvalidOperationException("Completion build recovery lost its original operation.");
        // A retry of the same request adopts the durable winner, including after a restart.
        if (original.RecoversOperationId == target.OperationId) return original;
        if (!target.Matches(original) || task.Status != WorkTaskStatus.Failed ||
            task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester) ||
            task.LastDispatch?.DispatchedAt != original.DispatchAt ||
            original is not { Phase: CompletionBuildEvidencePhase.ApparatusFailure, Applied: true, CustodyReleased: true, RecoversOperationId: null })
            throw new InvalidOperationException("Completion build recovery lost its failed dispatch authority.");
        ValidateOrigin(task, goalId, original);
        using var owner = Process.GetCurrentProcess();
        var id = Guid.NewGuid().ToString("N");
        var recovery = original with
        {
            Id = id, RecoversOperationId = original.Id,
            Deadline = _utcNow() + OrchestratorBuildEvidenceCheck.DefaultTimeout + _remoteBuildBudget(original.WorktreeRoot),
            ResultPath = Path.Combine(DotnetBuildStorageLayout.GoalArtifactsPath(goalId, _storageRoot),
                "completion-build-evidence", id + ".result.json"),
            LaunchOwnerPid = Environment.ProcessId, LaunchOwnerStartedAt = new DateTimeOffset(owner.StartTime.ToUniversalTime()),
            Phase = CompletionBuildEvidencePhase.WaitingForLease, ChildPid = null, ChildStartedAt = null,
            ChildImagePath = null, LeaseOwner = null, BuildExitCode = null, Diagnostic = "",
            Applied = false, CustodyReleased = false
        };
        return Store(kernel, recovery, original);
    }
}
