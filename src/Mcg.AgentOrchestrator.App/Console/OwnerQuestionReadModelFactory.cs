using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerQuestionReadModelFactory
{
    internal static OwnerQuestionReadModel Create(IOrchestratorStateQueries state, string orchestratorDirectory,
        OwnerConsoleRefreshGoalSnapshots? snapshots = null, Func<string?>? changeMarker = null) =>
        new OwnerQuestionReadModel(state, orchestratorDirectory, snapshots, changeMarker);

    internal static OwnerQuestionReadModel ForWorkspace(OrchestratorWorkspace workspace) =>
        Create(File.Exists(workspace.SqliteStatePath)
            ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            : new EmptyOwnerConsoleStateQueries(), workspace.OrchestratorDirectory);
}
