using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorModelBindingRefresh
{
    void RefreshBindings();
}

// Refreshed by the owning tick, captured before scheduling background work. Active work
// holds its catalog (including fallback) across later refreshes; hosts and claims survive.
// The instance-scoped ExecutionContext value flows into the host's existing Task.Run,
// so even queued work captures the dispatching tick rather than a later mutable value.
internal sealed class ConductorModelBindingSnapshot(ModelFunctionCatalog? catalog,
    Func<ModelFunctionCatalog>? loader = null) : IConductorModelBindingRefresh
{
    private readonly ModelFunctionCatalog _initial = Copy(catalog ?? ModelFunctionCatalog.Empty);
    private readonly AsyncLocal<ModelFunctionCatalog?> _tick = new();
    internal ModelFunctionCatalog Current => _tick.Value ?? _initial;
    public void RefreshBindings()
    {
        if (loader is not null) _tick.Value = Copy(loader());
    }
    private static ModelFunctionCatalog Copy(ModelFunctionCatalog value) =>
        value.Bindings is null ? value : new(value.Bindings.ToArray());
}
