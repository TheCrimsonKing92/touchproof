using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal sealed partial class GoalDispatchOperations
{
    internal static WorkerToolsInstaller? CreateWorkerToolsInstaller(OrchestratorHome home)
    {
        var config = Path.Combine(home.InstallRootDirectory, "config", "worker-tools.json");
        if (!File.Exists(config))
            config = Path.Combine(AppContext.BaseDirectory, "config", "worker-tools.json");
        return File.Exists(config)
            ? new WorkerToolsInstaller(config, Path.Combine(OrchestratorDataRoot.ResolveDefaultDirectory(), "tools", "worker-tools", "installs"))
            : null;
    }
}
