using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Stable build slots ration concurrent dotnet build and test processes, capped at 2.
// This advisory check reserves nothing and does not change whether focused evidence needs a slot.
internal static class PreReviewEvidenceLaunchCapacity
{
    internal static bool MayStart(WorkerBuildCapacitySnapshot snapshot) =>
        WorkerBuildCapacityGate.Decide(snapshot, capacityDispatchCount: 0) == WorkerBuildCapacityVerdict.Retry;

    internal static Func<GoalId, WorkerBuildCapacitySnapshot> CreateHostReader(DotnetBuildStorageRoot? storageRoot)
    {
        var root = storageRoot ?? DotnetBuildEnvironmentManager.CaptureStorageRoot();
        return goalId => WorkerBuildCapacityGate.ReadHostSnapshot(root, TimeProvider.System,
            WorkerBuildCapacityGate.IsExecutionLockHeld, FocusedEvidencePermitTicket.Key(goalId));
    }
}
