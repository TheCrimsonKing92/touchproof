namespace Mcg.AgentOrchestrator.App.Orchestration;

// Grouped gates require direct delivery, even when an override supplies the gate runner.
internal static class GroupedAcceptanceAdmission
{
    internal static bool IsEnabled(OrchestratorWorkspace? workspace, bool hasRunnerOverride,
        bool dependenciesAvailable) =>
        !PullRequestLandingDelivery.Applies(workspace) && (hasRunnerOverride || dependenciesAvailable);
}
