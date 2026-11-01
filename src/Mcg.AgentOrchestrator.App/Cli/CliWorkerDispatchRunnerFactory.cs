using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliWorkerDispatchRunnerFactory
{
    internal static BackgroundDispatchRunner Create(OrchestratorWorkspace workspace) => new()
    {
        NodeDependencyInstall = WorkerNodeDependencyInstallSource.For(workspace.ProjectHomeDirectoryOrNull),
        DeclaredToolHomes = () => AcceptanceGateEngineSettings.Load(
            workspace.RootDirectory, workspace.ProjectHomeDirectoryOrNull).DeclaredToolHomes
    };
}
