using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorPanelBindingSnapshot
{
    internal static IReadOnlyList<IConductorPanelJudgeRunner> Load(Func<ModelFunctionCatalog> loader)
    {
        ModelFunctionCatalog catalog;
        try { catalog = loader(); }
        // Preserve the existing malformed catalog handling at the panel boundary.
        catch (NullReferenceException) { catalog = new(null!); }
        return [new CodexSolPanelJudgeRunner(catalog), new ClaudeSonnetPanelJudgeRunner(catalog)];
    }
}
