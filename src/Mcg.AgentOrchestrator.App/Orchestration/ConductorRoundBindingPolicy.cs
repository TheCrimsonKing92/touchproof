using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorRoundBindingPolicy
{
    internal static bool SupportsSubscriptionProviders(string purpose) => new[]
    {
        ModelFunctionPurposes.ConductorAuthor, ModelFunctionPurposes.ConductorSteward,
        ModelFunctionPurposes.StewardTriage, ModelFunctionPurposes.PlanSampler,
        ModelFunctionPurposes.BoardFillVerifier
    }.Contains(purpose, StringComparer.OrdinalIgnoreCase);
}
