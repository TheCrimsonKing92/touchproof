using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorRoundInput(
    ConductorAuthorItem Item, string GoalBrief, string RefinedSpec,
    SpecRefinerPrecedent? MatchingPrecedent, ConductorLessonSelection? Lessons = null,
    int Attempt = 1, Func<bool>? TryConsumeFallbackAttempt = null);

internal interface IConductorAuthorModelRound
{
    string? ModelAlias => null;
    Task<string> DispatchAsync(ConductorAuthorRoundInput input, string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class ClaudeConductorAuthorModelRound(
    string receiptDirectory,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? runProcessAsync = null,
    ModelFunctionCatalog? catalog = null,
    Func<string, bool>? launcherAvailable = null,
    Func<ModelFunctionCatalog>? catalogLoader = null)
    : IConductorAuthorModelRound, IConductorModelBindingRefresh
{
    private readonly ConductorModelBindingSnapshot _bindings = new(catalog, catalogLoader);
    public void RefreshBindings() => _bindings.RefreshBindings();

    private readonly Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> _runProcessAsync =
        runProcessAsync ?? WorkerProcessRunner.RunBufferedAsync;
    private readonly Func<string, bool> _launcherAvailable = launcherAvailable ?? WorkerProfileDiagnostics.LocalCommandExists;

    public string? ModelAlias => ConductorRoundModelResolver.Resolve(_bindings.Current, ModelFunctionPurposes.ConductorAuthor).Alias;

    public async Task<string> DispatchAsync(ConductorAuthorRoundInput input,
        string workingDirectory, CancellationToken cancellationToken)
    {
        var snapshot = _bindings.Current;
        var model = ConductorRoundModelResolver.Resolve(snapshot, ModelFunctionPurposes.ConductorAuthor);
        var roundId = Guid.NewGuid().ToString("N");
        if (!model.IsValid)
        {
            WriteReceipt(input, roundId, input.Attempt, model, null, model.InvalidReason, null, null, model.InvalidReason);
            throw new ConductorModelRoundException(model.InvalidReason!, null, model.InvalidReason);
        }
        var prompt = ConductorAuthorPrompt.Render(input);
        var execution = await ConductorSubscriptionRoundExecutor.RunAsync(snapshot,
            ModelFunctionPurposes.ConductorAuthor, "author", prompt, workingDirectory, TimeSpan.FromMinutes(10),
            selected => $"claude {selected.ModelArguments} --permission-mode plan -p", _runProcessAsync,
            ConductorSubscriptionRoundExecutor.AuthorFailure,
            cancellationToken, _launcherAvailable, input.Attempt, ConductorAuthorHost.MaxAttempts,
            input.TryConsumeFallbackAttempt,
            invocation => WriteReceipt(input, roundId, invocation.Attempt, invocation.Model,
                invocation.Result, invocation.Failure, invocation.PrimaryReceipt, invocation.PrimaryExhaustion,
                invocation.TerminalReason, invocation.Exhaustion));
        return execution.RequireAnswer();
    }

    private string WriteReceipt(ConductorAuthorRoundInput input, string roundId, int attempt,
        ConductorRoundModel identity, WorkerProcessRunResult? result, string? failure,
        string? primaryReceipt, ConductorProviderExhaustion? primaryExhaustion, string? terminalReason,
        ConductorProviderExhaustion? exhaustion = null)
    {
        Directory.CreateDirectory(receiptDirectory);
        var path = Path.Combine(receiptDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            item = input.Item.Identity, completedAt = DateTimeOffset.UtcNow,
            exitCode = result?.ExitCode, stdout = result?.StandardOutput, stderr = result?.StandardError,
            failure, model = identity.Alias, lessonIds = input.Lessons?.LessonIds.ToArray() ?? [],
            launcher = identity.IsValid ? identity.WorkerProfileName : null, reasoningEffort = identity.ReasoningEffort,
            roundId, attempt, primaryReceipt, primaryExhaustion, exhaustion, terminalReason,
            timedOut = result?.TimedOut, captureLimited = result?.CaptureLimited
        }));
        return path;
    }

}
