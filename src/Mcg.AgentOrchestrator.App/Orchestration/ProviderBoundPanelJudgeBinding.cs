using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ProviderBoundPanelJudgeBinding
{
    internal static bool Supported(string? profile) =>
        string.Equals(profile, "codex-cli", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(profile, "claude-cli", StringComparison.OrdinalIgnoreCase);

    internal static PanelJudgeBinding Resolve(ModelFunctionCatalog catalog, string purpose)
    {
        if (catalog.Bindings is null || catalog.Bindings.Any(binding => binding is null))
            return new(null, null, $"panel-binding-invalid:{purpose} binding=catalog cause=malformed-catalog");
        var knownPurpose = purpose is ModelFunctionPurposes.PanelJudgeSol or ModelFunctionPurposes.PanelJudgeSonnet;
        var bindings = catalog.ForPurpose(purpose);
        var subscription = bindings.Count == 1 ? bindings[0].Subscription : null;
        var cause = !knownPurpose ? "unknown-purpose" : bindings.Count != 1 ? "expected-one-binding" :
            subscription is null ? "missing-subscription" :
            !Supported(subscription.WorkerProfileName) ? "wrong-profile" :
            string.IsNullOrWhiteSpace(subscription.ModelAlias) ? "empty-model-alias" :
            subscription.ModelAlias.Any(char.IsControl) ? "malformed-model-alias" :
            subscription.ReasoningEffort is { Length: > 0 } effort && effort is not
                ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max") ? "invalid-effort" : null;
        return cause is null ? new(subscription!.ModelAlias!.Trim(), subscription.ReasoningEffort, null, subscription.WorkerProfileName.ToLowerInvariant()) :
            new(null, null, $"panel-binding-invalid:{purpose} binding={string.Join(',', bindings.Select(binding => binding.Name ?? binding.Purpose))} cause={cause}");
    }
}
