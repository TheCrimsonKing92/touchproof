using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelHost
{
    internal static ConductorJudgePanelHost CreateDefault(OrchestratorWorkspace workspace)
    {
        IReadOnlyList<IConductorPanelJudgeRunner> LoadJudges() =>
            ConductorPanelBindingSnapshot.Load(() => ModelFunctionCatalogStore.Load(workspace.ModelFunctionCatalogPath));
        var judges = LoadJudges();
        var store = new ConductorJudgePanelCaseStore(Path.Combine(workspace.OrchestratorDirectory, "judge-panel.db"));
        return new(store,
            judges[0], judges[1],
            goal => ResolveCandidate(workspace, goal), new ConductEventLogWriter(workspace.ConductEventsLogPath),
            judgeLoader: LoadJudges)
        {
            Triggers = new(store, new(workspace.GoalLifecycleEventsDirectory, workspace.ConductEventsLogPath,
                Path.Combine(workspace.OrchestratorDirectory, "author-claims.db"),
                Path.Combine(workspace.OrchestratorDirectory, "cohort-acceptance.db"), workspace.SqliteStatePath),
                new(workspace.ExecutionDirectory))
        };
    }

    private static string? ResolveCandidate(OrchestratorWorkspace workspace, Goal goal)
    {
        var directory = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
        if (directory is null) return null;
        try
        {
            var result = GitCli.Run(directory, "rev-parse", "HEAD");
            var sha = result.Output.Trim();
            return result.Succeeded && sha.Length == 40 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
