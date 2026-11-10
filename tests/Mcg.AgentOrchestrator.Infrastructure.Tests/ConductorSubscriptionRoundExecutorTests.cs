using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake launchers/file listers and uniquely owned receipt directories.
public sealed class ConductorSubscriptionRoundExecutorTests
{
    internal static ModelFunctionCatalog Catalog(string purpose, bool codexFirst = true) => new([
        ConductorRoundModelResolverTests.Binding(purpose) with
        {
            Subscription = codexFirst ? new("codex-cli", "gpt-6.1-sol", "high") : new("claude-cli", "sonnet"),
            SubscriptionFallback = codexFirst ? new("claude-cli", "sonnet") : new("codex-cli", "gpt-6.1-sol", "high")
        }]);

    internal static ConductorStewardTrigger Trigger() => new("goal", "task", "candidate",
        ConductorStewardTriggerKind.DeveloperNoChangeWithConfirmedRed,
        new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), "failure", "", [], ["criterion"]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Steward_and_sampler_use_selected_provider_and_decode_Codex(bool sampler, bool failPrimary)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var requests = new List<WorkerProcessRunRequest>();
        var purpose = sampler ? ModelFunctionPurposes.PlanSampler : ModelFunctionPurposes.ConductorSteward;
        Task<WorkerProcessRunResult> Run(WorkerProcessRunRequest request, CancellationToken _)
        {
            requests.Add(request);
            if (failPrimary && requests.Count == 1)
                return Task.FromResult(new WorkerProcessRunResult(1, "", "ERROR: reached your usage limit; try again later due to usage"));
            return Task.FromResult(new WorkerProcessRunResult(0, request.Command.StartsWith("codex")
                ? ConductorAuthorModelRoundCatalogTests.CodexAnswer("answer") : "answer", ""));
        }
        var catalog = Catalog(purpose);
        var answer = sampler
            ? Assert.IsType<PlanDecompositionSampleResult.Succeeded>(await PlanDecompositionSampleRound.RunAsync(
                "prompt", fixture.Root, catalog, Run, launcherAvailable: _ => true)).StandardOutput
            : await new ClaudeConductorStewardModelRound(fixture.Receipts, Run, new EmptyFiles(),
                (_, _) => true, catalog: catalog, launcherAvailable: _ => true)
                .DispatchAsync(Trigger(), fixture.Root, CancellationToken.None);
        Assert.Equal("answer", answer);
        Assert.Equal(failPrimary ? 2 : 1, requests.Count);
        Assert.StartsWith("codex exec", requests[0].Command);
        Assert.Contains("--model 'gpt-6.1-sol'", requests[0].Command);
        Assert.Contains("--sandbox 'read-only'", requests[0].Command);
        Assert.Contains("model_reasoning_effort='high'", requests[0].Command);
        Assert.Equal(sampler ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(4), requests[0].Timeout);
        Assert.Equal(fixture.Root, requests[0].WorkingDirectory);
        if (failPrimary)
        {
            Assert.StartsWith("claude ", requests[1].Command);
            Assert.Equal(requests[0].StandardInput, requests[1].StandardInput);
            Assert.Equal(requests[0].Timeout, requests[1].Timeout);
            if (!sampler) Assert.Contains("--tools 'Read,Grep,Glob'", requests[1].Command);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_Claude_falls_back_to_Codex_once(bool sampler)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var purpose = sampler ? ModelFunctionPurposes.PlanSampler : ModelFunctionPurposes.ConductorSteward;
        var commands = new List<string>();
        Task<WorkerProcessRunResult> Run(WorkerProcessRunRequest request, CancellationToken _)
        {
            commands.Add(request.Command);
            return Task.FromResult(commands.Count == 1
                ? new WorkerProcessRunResult(1, ConductorAuthorModelRoundCatalogTests.Exhausted, "")
                : new WorkerProcessRunResult(0, ConductorAuthorModelRoundCatalogTests.CodexAnswer("alternate"), ""));
        }
        var catalog = Catalog(purpose, false);
        var answer = sampler
            ? Assert.IsType<PlanDecompositionSampleResult.Succeeded>(await PlanDecompositionSampleRound.RunAsync(
                "prompt", fixture.Root, catalog, Run, launcherAvailable: _ => true)).StandardOutput
            : await new ClaudeConductorStewardModelRound(fixture.Receipts, Run, new EmptyFiles(),
                (_, _) => true, catalog: catalog, launcherAvailable: _ => true)
                .DispatchAsync(Trigger(), fixture.Root, CancellationToken.None);
        Assert.Equal("alternate", answer);
        Assert.Equal(2, commands.Count);
        Assert.StartsWith("claude ", commands[0]);
        Assert.StartsWith("codex exec", commands[1]);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("timeout")]
    [InlineData("capture")]
    [InlineData("transport")]
    [InlineData("assistant-text")]
    [InlineData("missing-fallback")]
    [InlineData("same-provider")]
    [InlineData("unavailable")]
    [InlineData("double-exhaustion")]
    public async Task Alternate_eligibility_is_bounded_and_requires_authoritative_exhaustion(string scenario)
    {
        var catalog = Catalog(ModelFunctionPurposes.PlanSampler);
        if (scenario is "missing-fallback" or "same-provider")
            catalog = new([catalog.Bindings[0] with { SubscriptionFallback = scenario == "same-provider"
                ? new("codex-cli", "gpt-6.1-sol") : null }]);
        var calls = 0;
        var limit = "ERROR: reached your usage limit; try again later due to usage";
        var execution = await ConductorSubscriptionRoundExecutor.RunAsync(catalog,
            ModelFunctionPurposes.PlanSampler, "sample", "prompt", "root", TimeSpan.FromMinutes(10),
            model => $"claude {model.ModelArguments} -p", (_, _) =>
            {
                calls++;
                return Task.FromResult(scenario switch
                {
                    "ordinary" => new WorkerProcessRunResult(1, "", "ordinary error"),
                    "timeout" => new WorkerProcessRunResult(1, "", limit, TimedOut: true),
                    "capture" => new WorkerProcessRunResult(1, "", limit, CaptureLimited: true),
                    "transport" => new WorkerProcessRunResult(0, "malformed transport", ""),
                    "assistant-text" => new WorkerProcessRunResult(0,
                        ConductorAuthorModelRoundCatalogTests.CodexAnswer(limit), ""),
                    _ => new WorkerProcessRunResult(1, "", limit)
                });
            }, result => result.ExitCode != 0 ? "failed" : null, CancellationToken.None,
            _ => scenario != "unavailable", reserveFallback: () => true);
        Assert.Equal(scenario == "double-exhaustion" ? 2 : 1, calls);
        if (scenario == "assistant-text") Assert.Equal(limit, execution.RequireAnswer());
        else Assert.NotNull(execution.Failure);
        if (scenario == "double-exhaustion") Assert.Equal("sample-attempt-budget-exhausted", execution.TerminalReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_launch_exception_do_not_invoke_an_alternate(bool cancel)
    {
        var calls = 0;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => ConductorSubscriptionRoundExecutor.RunAsync(
            Catalog(ModelFunctionPurposes.PlanSampler), ModelFunctionPurposes.PlanSampler, "sample", "prompt", "root",
            TimeSpan.FromMinutes(10), model => $"claude {model.ModelArguments} -p", (_, _) =>
            {
                calls++;
                return Task.FromException<WorkerProcessRunResult>(cancel ? new OperationCanceledException() : new IOException("launch failed"));
            }, _ => null, CancellationToken.None, _ => true, reserveFallback: () => true));
        Assert.Equal(cancel ? typeof(OperationCanceledException) : typeof(IOException), error.GetType());
        Assert.Equal(1, calls);
    }

    internal sealed class EmptyFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) => [];
    }
}
