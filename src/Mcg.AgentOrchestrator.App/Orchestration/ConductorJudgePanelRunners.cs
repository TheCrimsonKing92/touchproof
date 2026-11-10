using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorPanelJudgeRunner
{
    string Judge { get; }
    string? BindingError { get; }
    string? WorkerProfileName => null;
    Task<PanelJudgeResult> RunAsync(PanelCase item, CancellationToken cancellationToken);
}

internal sealed record PanelJudgeBinding(string? Alias, string? Effort, string? Error, string? WorkerProfileName = null);
internal static class PanelJudgeBindingResolver
{
    internal static PanelJudgeBinding Resolve(ModelFunctionCatalog catalog, string purpose)
    {
        return ProviderBoundPanelJudgeBinding.Resolve(catalog, purpose);
    }
}

internal abstract class ConductorPanelJudgeRunner : IConductorPanelJudgeRunner
{
    protected readonly PanelJudgeBinding Binding;
    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>> _process;
    protected ConductorPanelJudgeRunner(ModelFunctionCatalog catalog, string purpose,
        Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process)
    {
        Binding = PanelJudgeBindingResolver.Resolve(catalog, purpose);
        _process = process ?? PanelJudgeProcess.RunAsync;
    }
    public abstract string Judge { get; }
    public string? BindingError => Binding.Error;
    public string? WorkerProfileName => Binding.WorkerProfileName;
    protected abstract string Command(string directory);
    protected virtual (string Answer, string? Usage, bool ProviderFault) Decode(string stdout, string stderr) => (stdout, null, false);
    protected static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public Task<PanelJudgeResult> RunAsync(PanelCase item, CancellationToken cancellationToken) =>
        PanelJudgeInvocation.RunAsync(Judge, Binding, Command, Decode, _process, item, cancellationToken);
}

internal sealed class CodexSolPanelJudgeRunner(ModelFunctionCatalog catalog,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null)
    : ProviderBoundPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSol, "sol", process) { }

internal sealed class ClaudeSonnetPanelJudgeRunner(ModelFunctionCatalog catalog,
    Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>>? process = null)
    : ProviderBoundPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSonnet, "sonnet", process) { }
