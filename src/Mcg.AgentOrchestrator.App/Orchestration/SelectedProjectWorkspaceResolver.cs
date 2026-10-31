namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SelectedProjectWorkspaceResolver
{
    public static OrchestratorWorkspace Resolve(OrchestratorProject project, string? executionDirectory,
        string? tenantName, OrchestratorProjectRegistry registry) =>
        project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
            ? OrchestratorWorkspace.ForDirectory(project.RootDirectory,
                string.IsNullOrWhiteSpace(executionDirectory) ? null : executionDirectory, tenantName, registry)
            : project.ResolveWorkspace(tenantName);
}
