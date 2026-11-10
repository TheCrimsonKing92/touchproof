using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake provider processes; the ledger and conduct log are unique to each harness.
public sealed class ConductorPanelProviderAvailabilityTests
{
    internal static ModelFunctionCatalog Catalog(string first = "claude-cli", string second = "codex-cli") => new([
        ConductorRoundModelResolverTests.Binding(ModelFunctionPurposes.PanelJudgeSol) with { Subscription = new(first, "first-model") },
        ConductorRoundModelResolverTests.Binding(ModelFunctionPurposes.PanelJudgeSonnet) with { Subscription = new(second, "second-model") }
    ]);

    internal static ConductorJudgePanelHost Host(PanelTestHarness h, ModelFunctionCatalog catalog,
        Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>> first,
        Func<WorkerProcessRunRequest, CancellationToken, Task<PanelProcessResult>> second) => new(h.Store,
        new ProviderBoundPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSol, "seat-a", first),
        new ProviderBoundPanelJudgeRunner(catalog, ModelFunctionPurposes.PanelJudgeSonnet, "seat-b", second),
        _ => h.Candidate, new ConductEventLogWriter(h.ConductPath), () => h.Time.UtcNow);

    private static PanelProcessResult Valid(WorkerProcessRunRequest request) => new(0,
        PanelTestHarness.Answer(CaseId(request)), "");

    private static string CaseId(WorkerProcessRunRequest request)
    {
        const string prefix = "case_id=";
        var start = request.StandardInput!.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return request.StandardInput[start..].Split('.')[0];
    }

    [Theory]
    [InlineData("claude-cli", "codex-cli")]
    [InlineData("codex-cli", "claude-cli")]
    public async Task Stable_seats_accept_swapped_providers_and_arbitrary_judge_names(string first, string second)
    {
        using var h = new PanelTestHarness();
        var commands = new List<string>();
        var gate = new object();
        Task<PanelProcessResult> Run(WorkerProcessRunRequest request, CancellationToken _)
        {
            lock (gate) commands.Add(request.Command);
            return Task.FromResult(Valid(request));
        }
        var host = Host(h, Catalog(first, second), Run, Run);
        var item = h.Enqueue("providers");
        try { await h.Complete(host); }
        finally { host.Stop(); }
        Assert.Equal(2, commands.Count);
        Assert.Single(commands, command => command.StartsWith("codex exec"));
        Assert.Single(commands, command => command.StartsWith("claude -p"));
        Assert.Equal(PanelJudgmentOutcome.Complete, h.Store.Judgment(item.Id).Outcome);
        var results = h.Store.Results(item.Id);
        Assert.Equal(first, results.Single(result => result.Judge == "seat-a").WorkerProfileName);
        Assert.Equal(second, results.Single(result => result.Judge == "seat-b").WorkerProfileName);
        Assert.Equal("first-model", results.Single(result => result.Judge == "seat-a").ModelAlias);
        Assert.Equal("second-model", results.Single(result => result.Judge == "seat-b").ModelAlias);
        Assert.Contains("judgment=complete", File.ReadAllText(h.ConductPath));
    }

    [Theory]
    [InlineData("claude-cli")]
    [InlineData("codex-cli")]
    public async Task Same_provider_models_record_skips_without_launching(string profile)
    {
        using var h = new PanelTestHarness();
        var calls = 0;
        Task<PanelProcessResult> Run(WorkerProcessRunRequest _, CancellationToken __)
        { calls++; throw new InvalidOperationException("Same-provider panel must not launch"); }
        var host = Host(h, Catalog(profile, profile.ToUpperInvariant()), Run, Run);
        var item = h.Enqueue("same-provider");
        try { await h.Complete(host); }
        finally { host.Stop(); }
        Assert.Equal(0, calls);
        Assert.Equal(PanelJudgmentOutcome.Skipped, h.Store.Judgment(item.Id).Outcome);
        Assert.All(h.Store.Results(item.Id), result =>
        {
            Assert.Equal(PanelJudgeOutcome.Skipped, result.Outcome);
            Assert.Equal("provider-not-distinct", result.Reason);
        });
        Assert.Contains("provider-not-distinct", h.Store.Get(item.Id)!.Reason);
        Assert.Contains("judgment=skipped", File.ReadAllText(h.ConductPath));
    }

    [Theory(Timeout = 30000)]
    [Trait("Category", "CrossTick")]
    [InlineData("auth", "claude-cli")]
    [InlineData("usage", "claude-cli")]
    [InlineData("launch", "claude-cli")]
    [InlineData("timeout", "claude-cli")]
    [InlineData("cancel", "claude-cli")]
    [InlineData("auth", "codex-cli")]
    [InlineData("usage", "codex-cli")]
    [InlineData("launch", "codex-cli")]
    [InlineData("timeout", "codex-cli")]
    [InlineData("cancel", "codex-cli")]
    public async Task Runtime_unavailability_retains_partial_provenance_and_next_tick_progress(string failure, string profile)
    {
        using var h = new PanelTestHarness();
        var failedCalls = 0;
        var healthyCalls = 0;
        var observed = failure switch
        {
            "auth" => "authentication missing", "usage" => "provider usage limit exhausted",
            "launch" => "executable could not launch", "timeout" => "judge-timeout", _ => "cancelled"
        };
        Task<PanelProcessResult> Failing(WorkerProcessRunRequest request, CancellationToken _)
        {
            Interlocked.Increment(ref failedCalls);
            if (failure == "launch") throw new IOException(observed);
            if (failure == "cancel") throw new OperationCanceledException();
            return Task.FromResult(failure == "timeout"
                ? new PanelProcessResult(0, "", "", true)
                : new PanelProcessResult(1, "", observed));
        }
        Task<PanelProcessResult> Healthy(WorkerProcessRunRequest request, CancellationToken _)
        { Interlocked.Increment(ref healthyCalls); return Task.FromResult(Valid(request)); }
        var other = profile == "codex-cli" ? "claude-cli" : "codex-cli";
        var host = Host(h, Catalog(profile, other), Failing, Healthy);
        try
        {
            var first = h.Enqueue("first-runtime");
            await h.Complete(host);
            Assert.Equal(PanelJudgmentOutcome.Partial, h.Store.Judgment(first.Id).Outcome);
            Assert.Contains(observed, h.Store.Get(first.Id)!.Reason);
            var results = h.Store.Results(first.Id);
            var failed = results.Single(result => result.Judge == "seat-a");
            Assert.Equal(observed, failed.Reason);
            Assert.Equal(profile, failed.WorkerProfileName);
            Assert.Equal("first-model", failed.ModelAlias);
            var healthy = results.Single(result => result.Judge == "seat-b");
            Assert.Equal(PanelJudgeOutcome.Valid, healthy.Outcome);
            Assert.Equal(other, healthy.WorkerProfileName);
            Assert.Equal("second-model", healthy.ModelAlias);
            Assert.Equal(1, failedCalls);
            Assert.Equal(1, healthyCalls);
            var next = h.Enqueue("next-runtime");
            await h.Complete(host);
            Assert.Equal(PanelCaseTerminal.Completed, h.Store.Get(next.Id)!.Terminal);
            Assert.Equal(PanelJudgmentOutcome.Partial, h.Store.Judgment(next.Id).Outcome);
            Assert.Equal(2, failedCalls);
            Assert.Equal(2, healthyCalls);
            Assert.Equal(2, File.ReadAllLines(h.ConductPath).Length);
        }
        finally { host.Stop(); }
    }

    [Fact]
    public async Task Invalid_seat_keeps_binding_reason_and_never_fabricates_consensus()
    {
        using var h = new PanelTestHarness();
        var calls = 0;
        var catalog = Catalog("unsupported", "codex-cli");
        var host = Host(h, catalog, (_, _) => throw new InvalidOperationException("Invalid seat launched"),
            (request, _) => { calls++; return Task.FromResult(Valid(request)); });
        var item = h.Enqueue("invalid-seat");
        try { await h.Complete(host); }
        finally { host.Stop(); }
        Assert.Equal(1, calls);
        Assert.Equal(PanelJudgmentOutcome.Partial, h.Store.Judgment(item.Id).Outcome);
        Assert.Contains("cause=wrong-profile", h.Store.Get(item.Id)!.Reason);
        Assert.Equal(PanelJudgeOutcome.Skipped, h.Store.Results(item.Id).Single(result => result.Judge == "seat-a").Outcome);
    }

    [Fact]
    public void Historical_or_same_provider_results_cannot_establish_complete_judgment()
    {
        PanelJudgeResult first = new("a", PanelJudgeOutcome.Valid, 0, "", "");
        PanelJudgeResult second = new("b", PanelJudgeOutcome.Valid, 0, "", "");
        Assert.Equal(PanelJudgmentOutcome.Partial, PanelJudgmentCompleteness.Evaluate([first, second]).Outcome);
        first = first with { WorkerProfileName = "codex-cli" };
        second = second with { WorkerProfileName = "CODEX-CLI" };
        Assert.Contains("provider-not-distinct", PanelJudgmentCompleteness.Evaluate([first, second]).Reason);
    }
}
