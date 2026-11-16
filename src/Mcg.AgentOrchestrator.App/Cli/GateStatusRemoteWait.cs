using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GateStatusRemoteWait
{
    internal static void PrintText(OrchestratorWorkspace workspace)
    {
        var observedAt = DateTimeOffset.UtcNow;
        GateStatusTextView.Print(GateHeartbeatArtifacts.ReadStableSlots(observedAt), observedAt,
            Count(workspace, observedAt), Rows(workspace, observedAt));
    }

    internal static IReadOnlyList<GateStatusRemoteWaitDetail.Row> Rows(OrchestratorWorkspace? workspace,
        DateTimeOffset observedAt) => workspace is null ? [] : GateStatusRemoteWaitDetail.Read(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), observedAt,
            ConductorInvariantThresholds.GateIdleWhileReady).Where(row => !row.Stale).ToArray();

    internal static int Count(OrchestratorWorkspace? workspace, DateTimeOffset observedAt)
    {
        if (workspace is null) return 0;
        return ConductorAttemptHeartbeatScanner.CountLive(
            Path.Combine(workspace.OrchestratorDirectory, "acceptance-gate-attempts"), observedAt,
            ConductorInvariantThresholds.GateIdleWhileReady);
    }
}
