using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its projects root, state database and intent store.
// No environment mutation, shared console, wall-clock assertions or subprocesses.
public sealed class ClaudeUsageBackfillTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DryRun_MixedDispatches_ReportsCountsWithoutWrites()
    {
        using var f = new Fixture();
        var fill = f.Add("fill");
        var known = f.Add("known", usage: Reported(7, 3, 1));
        var missing = f.Add("missing", transcript: false, usage: DispatchProviderUsage.From(null, "historical-unknown"));
        var codex = f.Add("codex", provider: ProviderKind.OpenAICodexCli);
        await f.Save();
        var before = File.ReadAllBytes(f.Workspace.SqliteStatePath);
        Assert.Equal("Claude usage backfill: filled=1 already-known=1 skipped=1 skipped-reasons=claude-transcript-missing=1",
            f.Run().Trim());
        using var json = JsonDocument.Parse(f.Run("--json"));
        AssertCounts(json, 1, 1, 1);
        Assert.Equal(1, json.RootElement.GetProperty("skippedReasons").GetProperty("claude-transcript-missing").GetInt32());
        Assert.Equal(0, f.IntentOpens);
        Assert.Empty(await f.Intents());
        Assert.Equal(before, File.ReadAllBytes(f.Workspace.SqliteStatePath));
        Assert.Null(Usage(await f.Load(), fill));
        Assert.Equal(Reported(7, 3, 1), Usage(await f.Load(), known));
        AssertUnknown(Usage(await f.Load(), missing));
        Assert.Null(Usage(await f.Load(), codex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_StoredDispatch_FillsOnlyAfterTickAndFeedsContextUsage(bool receipt)
    {
        using var f = new Fixture();
        var goal = f.Add("fill", receipt: receipt);
        await f.Save();
        Assert.Empty(f.TickKernel.Goals); // Select the retained-goal transaction path.
        using var report = JsonDocument.Parse(f.Run("--apply", "--json"));
        AssertCounts(report, 1, 0, 0);
        var intent = Assert.Single(await f.Intents());
        Assert.Equal(OperatorIntentScopes.Workspace, intent.GoalId);
        Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Assert.Equal(intent.Id, report.RootElement.GetProperty("intentId").GetString());
        Assert.Equal($"operator-intent-status {intent.Id}", report.RootElement.GetProperty("poll").GetString());
        if (receipt) AssertUnknown(Usage(await f.Load(), goal));
        else Assert.Null(Usage(await f.Load(), goal));

        f.Tick();

        var stored = await f.Load();
        Assert.Equal(Reported(120, 100, 9), Usage(stored, goal));
        var dispatch = stored.GetGoal(goal.Id).Tasks.First().LastDispatch!;
        Assert.Equal(receipt, dispatch.ContextPackageReceipt is not null);
        if (receipt) Assert.Null(dispatch.ProviderUsage);
        var applied = (await f.Store.GetAsync(intent.Id))!;
        Assert.Equal(OperatorIntentStatus.Applied, applied.Status);
        Assert.Contains("filled=1 already-known=0 failed=0", applied.Outcome);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", "--json"], () => stored.Goals, output, error));
        Assert.Equal(string.Empty, error.ToString());
        using var usageReport = JsonDocument.Parse(output.ToString());
        var row = Assert.Single(usageReport.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal(1, row.GetProperty("reportedInputCount").GetInt32());
        Assert.Equal(120, row.GetProperty("input").GetProperty("median").GetDouble());
        Assert.Equal(100, row.GetProperty("cached").GetProperty("median").GetDouble());
        Assert.Equal(9, row.GetProperty("output").GetProperty("median").GetDouble());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Apply_StalePartialValues_PreservesReportedValues(bool receipt, bool workingSet)
    {
        using var f = new Fixture();
        var goal = f.Add("partial", receipt: receipt);
        await f.Save();
        f.Run("--apply");
        var current = await f.Load();
        var partial = new DispatchProviderUsage(ProviderUsageValue.Reported(777),
            ProviderUsageValue.Unknown("older-cache"), ProviderUsageValue.Reported(55));
        var task = current.GetGoal(goal.Id).Tasks.First();
        if (receipt)
            current.RecordDispatchContextPackageReceipt(goal.Id, task.Id, task.LastDispatch!.DispatchedAt,
                new("original-receipt", [], partial.InputTokens, partial.CachedInputTokens, partial.OutputTokens));
        else
            current.RecordDispatchProviderUsage(goal.Id, task.Id, task.LastDispatch!.DispatchedAt, partial);
        await f.Repository.SaveAsync(current);
        if (workingSet) f.TickKernel.ReplaceGoalWithSnapshot(current.ExportGoalSnapshot(goal.Id));

        f.Tick();

        var result = workingSet ? f.TickKernel : await f.Load();
        Assert.Equal(Reported(777, 100, 55), Usage(result, goal));
        if (receipt)
            Assert.Equal("original-receipt", result.GetGoal(goal.Id).Tasks.First().LastDispatch!.ContextPackageReceipt!.SemanticPackageId);
        Assert.Contains("filled=1 already-known=0 failed=0", Assert.Single(await f.Intents()).Outcome);
    }

    [Fact]
    public async Task Apply_TwoQueuedIntents_RechecksUsageAndSecondRunComputesNoFills()
    {
        using var f = new Fixture();
        var goal = f.Add("fill");
        var known = f.Add("known", usage: Reported(11, 4, 2));
        var codex = f.Add("codex", provider: ProviderKind.OpenAICodexCli, usage: Reported(88, 44, 22));
        var missing = f.Add("missing", transcript: false, receipt: true);
        await f.Save();
        f.Run("--apply");
        f.Run("--apply");
        Assert.Equal(2, (await f.Intents()).Count);
        f.Tick();
        var outcomes = (await f.Intents()).Select(intent => intent.Outcome).ToArray();
        Assert.Contains(outcomes, outcome => outcome!.Contains("filled=1 already-known=0 failed=0"));
        Assert.Contains(outcomes, outcome => outcome!.Contains("filled=0 already-known=1 failed=0"));
        var stored = await f.Load();
        Assert.Equal(Reported(120, 100, 9), Usage(stored, goal));
        Assert.Equal(Reported(11, 4, 2), Usage(stored, known));
        Assert.Equal(Reported(88, 44, 22), Usage(stored, codex));
        AssertUnknown(Usage(stored, missing));
        using var second = JsonDocument.Parse(f.Run("--apply", "--json"));
        AssertCounts(second, 0, 2, 1);
        f.Tick();
        Assert.All(await f.Intents(), intent => Assert.Equal(OperatorIntentStatus.Applied, intent.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_FullyReportedAfterSubmission_DoesNotOverwrite(bool receipt)
    {
        using var f = new Fixture();
        var goal = f.Add("fill", receipt: receipt);
        await f.Save();
        f.Run("--apply");
        var current = await f.Load();
        var task = current.GetGoal(goal.Id).Tasks.First();
        if (receipt)
            current.RecordDispatchContextPackageReceipt(goal.Id, task.Id, task.LastDispatch!.DispatchedAt,
                task.LastDispatch.ContextPackageReceipt!.WithProviderUsage(new ProviderReportedUsage(901, 903, 902)));
        else
            current.RecordDispatchProviderUsage(goal.Id, task.Id, task.LastDispatch!.DispatchedAt, Reported(901, 902, 903));
        await f.Repository.SaveAsync(current);
        f.Tick();
        Assert.Equal(Reported(901, 902, 903), Usage(await f.Load(), goal));
        Assert.Contains("filled=0 already-known=1 failed=0", Assert.Single(await f.Intents()).Outcome);
    }

    [Fact]
    public async Task Apply_BadTargets_CountsIndependentFailuresAndKeepsSuccess()
    {
        using var f = new Fixture();
        var good = f.Add("good");
        var codex = f.Add("codex", provider: ProviderKind.OpenAICodexCli);
        var ambiguous = f.Add("ambiguous", duplicate: true);
        await f.Save();
        var fills = new[]
        {
            Fill(good), Fill(codex), Fill(ambiguous),
            Fill(good) with { DispatchedAt = Start.AddYears(-1) },
            Fill(good) with { GoalId = Guid.NewGuid().ToString("N") }
        };
        await f.Enqueue(fills);
        f.Tick();
        var outcome = Assert.Single(await f.Intents());
        Assert.Equal(OperatorIntentStatus.Applied, outcome.Status);
        Assert.Contains("filled=1 already-known=0 failed=4", outcome.Outcome);
        foreach (var code in new[] { "dispatch-ambiguous", "dispatch-missing", "goal-missing", "not-claude-dispatch" })
            Assert.Contains(code + "=1", outcome.Outcome);
        Assert.Equal(Reported(120, 100, 9), Usage(await f.Load(), good));
        Assert.Null(Usage(await f.Load(), codex));
        Assert.All((await f.Load()).GetGoal(ambiguous.Id).Tasks, task => Assert.Null(task.LastDispatch!.ProviderUsage));
    }

    [Fact]
    public async Task Run_FiltersAndMalformedArguments_ValidateBeforeEnqueue()
    {
        using var f = new Fixture();
        var goal = f.Add("selected");
        f.Add("other");
        await f.Save();
        using var inclusive = JsonDocument.Parse(f.Run("--goal", goal.Id.Value, "--since", "2026-11-01T07:00:00-05:00", "--json"));
        AssertCounts(inclusive, 1, 0, 0);
        using var exclusive = JsonDocument.Parse(f.Run("--since", "2026-11-01T12:00:01Z", "--apply", "--json"));
        AssertCounts(exclusive, 0, 0, 0);
        Assert.Empty(JsonSerializer.Deserialize<ClaudeUsageBackfillOperatorIntentPayload>(
            Assert.Single(await f.Intents()).PayloadJson, OperatorIntentJson.Options)!.Fills);
        foreach (var args in new string[][] { ["--bogus"], ["--since", "2026-11-01"], ["--goal"] })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, CliClaudeUsageBackfillCommand.Run(["claude-usage-backfill", .. args, "--apply"],
                () => throw new InvalidOperationException("must not load"), f.Reader,
                () => throw new InvalidOperationException("must not enqueue"), output, error));
            Assert.Contains(CliCommandHelp.ClaudeUsageBackfillUsage, error.ToString());
        }
        using var invalidOutput = new StringWriter();
        using var invalidError = new StringWriter();
        foreach (var prefix in new[] { "absent", "" })
        {
            Assert.Equal(1, CliClaudeUsageBackfillCommand.Run(["claude-usage-backfill", "--goal", prefix, "--apply"],
                () => f.Kernel.Goals, f.Reader, () => throw new InvalidOperationException("must not enqueue"), invalidOutput, invalidError));
        }
        Assert.Equal(1, CliClaudeUsageBackfillCommand.Run(["claude-usage-backfill", "--goal", goal.Id.Value, "--apply"],
            () => [goal, goal], f.Reader, () => throw new InvalidOperationException("must not enqueue"), invalidOutput, invalidError));
        Assert.Contains("ambiguous-goal-prefix", invalidError.ToString());
        Assert.Single(await f.Intents());
    }

    [Fact]
    public async Task Run_ReaderFailures_PreservesReasonsAndSearchesSandboxRoot()
    {
        using var f = new Fixture();
        var sandbox = f.Add("sandbox", transcript: false, sandbox: true);
        f.Write(Path.Combine(f.Root, ".mcg-sandbox", "instance", "claude-config", "projects"), "sandbox");
        f.Add("no-usage", transcript: false);
        ClaudeTranscriptUsageReaderTests.Write(f.Root, "no-usage", "{}");
        f.Add("unreadable");
        f.Add(null, transcript: false);
        await f.Save();
        f.Reader = new([f.Root], path => path.EndsWith("unreadable.jsonl", StringComparison.Ordinal)
            ? throw new IOException("fixture") : File.OpenRead(path));
        using var report = JsonDocument.Parse(f.Run("--json"));
        AssertCounts(report, 1, 0, 3);
        var reasons = report.RootElement.GetProperty("skippedReasons");
        foreach (var code in new[] { "claude-transcript-missing", "claude-transcript-unreadable", "claude-transcript-no-usage" })
            Assert.Equal(1, reasons.GetProperty(code).GetInt32());
        // No default root sees the sandbox transcript: ReadDispatch must supply it.
        f.Reader = new([]);
        using var sandboxOnly = JsonDocument.Parse(f.Run("--goal", sandbox.Id.Value, "--json"));
        AssertCounts(sandboxOnly, 1, 0, 0);
    }

    [Fact]
    public async Task Apply_NegativePayload_RejectsBeforeAnyMutation()
    {
        using var f = new Fixture();
        var goal = f.Add("fill");
        await f.Save();
        await f.Enqueue([Fill(goal), Fill(goal) with { InputTokens = -1 }]);
        f.Tick();
        var intent = Assert.Single(await f.Intents());
        Assert.Equal(OperatorIntentStatus.Rejected, intent.Status);
        Assert.Contains("invalid-payload", intent.Outcome);
        Assert.Null(Usage(await f.Load(), goal));
    }

    [Fact]
    public async Task Apply_ReportedZeros_AreLegitimateCounts()
    {
        using var f = new Fixture();
        var goal = f.Add("zero");
        ClaudeTranscriptUsageReaderTests.Write(f.Root, "zero", """
            {"type":"assistant","message":{"id":"a","usage":{"input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0}}}
            """);
        await f.Save();
        f.Run("--apply");
        f.Tick();
        Assert.Equal(Reported(0, 0, 0), Usage(await f.Load(), goal));
    }

    [Fact]
    public async Task Apply_OneStoreFailure_ContinuesToOtherGoals()
    {
        using var f = new Fixture();
        var failed = f.Add("failed");
        var good = f.Add("good");
        await f.Save();
        await f.Enqueue([Fill(failed), Fill(good)]);
        var opens = 0;
        new OperatorIntentCoordinator(f.Store)
        {
            ClaudeUsageBackfills = new(() => ++opens == 1 ? throw new IOException("fixture") : f.Repository)
        }.ExecuteWorkspacePending(f.TickKernel);
        Assert.Equal(2, opens);
        Assert.Null(Usage(await f.Load(), failed));
        Assert.Equal(Reported(120, 100, 9), Usage(await f.Load(), good));
        var intent = Assert.Single(await f.Intents());
        Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Assert.Contains("filled=1 already-known=0 failed=1 failed-reasons=store-error=1", intent.Outcome);
    }

    [Fact]
    public void Catalog_HelpAndFlags_ExposeBackfill()
    {
        Assert.Contains("claude-usage-backfill", CliArgumentParser.RecognizedCommands);
        Assert.True(CliClaudeUsageBackfillCommand.IsCommand(["CLAUDE-USAGE-BACKFILL"]));
        CliCommandHelp.ThrowIfInvalidFlags(["claude-usage-backfill", "--since", "2026-11-01T00:00:00Z", "--goal", "a", "--apply", "--json"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["claude-usage-backfill", "--bogus"]));
    }

    private static void AssertCounts(JsonDocument document, int filled, int known, int skipped)
    {
        Assert.Equal(filled, document.RootElement.GetProperty("filled").GetInt32());
        Assert.Equal(known, document.RootElement.GetProperty("alreadyKnown").GetInt32());
        Assert.Equal(skipped, document.RootElement.GetProperty("skipped").GetInt32());
    }

    private static DispatchProviderUsage Reported(long input, long cached, long output) => new(
        ProviderUsageValue.Reported(input), ProviderUsageValue.Reported(cached), ProviderUsageValue.Reported(output));
    private static ClaudeUsageBackfillFill Fill(Goal goal) => new(goal.Id.Value,
        goal.Tasks.First().LastDispatch!.DispatchedAt, 120, 100, 9);
    private static DispatchProviderUsage? Usage(AgentOrchestratorKernel kernel, Goal goal) =>
        ContextUsageFigures.RecordedUsage(kernel.GetGoal(goal.Id).Tasks.First().LastDispatch!);
    private static void AssertUnknown(DispatchProviderUsage? usage)
    {
        Assert.NotNull(usage);
        Assert.All(new[] { usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens }, value =>
        {
            Assert.Equal(ProviderUsageState.Unknown, value.State);
            Assert.Null(value.Value);
            Assert.Equal("historical-unknown", value.UnknownReason);
        });
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "claude-backfill-" + Guid.NewGuid().ToString("N"));
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly AgentOrchestratorKernel TickKernel = new();
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly SqliteOrchestratorStateRepository Repository;
        internal readonly SqliteOperatorIntentStore Store;
        internal ClaudeTranscriptUsageReader Reader;
        internal int IntentOpens;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            Store = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Reader = new([Root]);
        }
        internal Goal Add(string? session, bool receipt = false, bool transcript = true,
            ProviderKind provider = ProviderKind.AnthropicClaudeCli, DispatchProviderUsage? usage = null,
            bool duplicate = false, bool sandbox = false)
        {
            if (transcript && session is not null) Write(Root, session);
            var tasks = Enumerable.Range(0, duplicate ? 2 : 1)
                .Select(_ => new TaskSpec(TaskId.New(), "Historical dispatch", AgentRole.Developer)).ToArray();
            var goal = Kernel.CreateGoal("Backfill fixture", tasks);
            Kernel.ActivateGoal(goal.Id, ConductorDriverTests.DefaultAgents());
            var unknown = ProviderUsageValue.Unknown("historical-unknown");
            foreach (var task in tasks)
                Kernel.RecordTaskDispatch(goal.Id, task.Id, new("developer", "claude -p", Root, Start,
                    ProviderName: "Anthropic", ModelName: "claude-opus-5-5", SandboxLowIntegrity: sandbox,
                    WorkerProviderKind: provider, ProviderSessionId: session, ProviderUsage: receipt ? null : usage,
                    ContextPackageReceipt: receipt ? new("original-receipt", [],
                        usage?.InputTokens ?? unknown, usage?.CachedInputTokens ?? unknown, usage?.OutputTokens ?? unknown) : null));
            return goal;
        }
        internal void Write(string root, string session) => ClaudeTranscriptUsageReaderTests.Write(root, session, """
            {"type":"assistant","message":{"id":"a","usage":{"input_tokens":20,"cache_read_input_tokens":100,"output_tokens":9}}}
            """);
        internal Task Save() => Repository.SaveAsync(Kernel);
        internal Task<AgentOrchestratorKernel> Load() => SqliteOrchestratorStateRepository.OpenReadOnly(Workspace.SqliteStatePath).LoadAsync();
        internal Task<IReadOnlyList<OperatorIntentRecord>> Intents() => Store.ListForGoalAsync(OperatorIntentScopes.Workspace);
        internal string Run(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = CliClaudeUsageBackfillCommand.Run(["claude-usage-backfill", .. args],
                () => Load().GetAwaiter().GetResult().Goals, Reader, () => { IntentOpens++; return Store; }, output, error);
            Assert.Equal(string.Empty, error.ToString());
            Assert.Equal(0, exit);
            return output.ToString();
        }
        internal Task<OperatorIntentRecord> Enqueue(IReadOnlyList<ClaudeUsageBackfillFill> fills)
        {
            var id = Guid.NewGuid().ToString("N");
            return Store.EnqueueAsync(new(id, id, OperatorIntentVerbs.ClaudeUsageBackfill, OperatorIntentScopes.Workspace,
                null, JsonSerializer.Serialize(new ClaudeUsageBackfillOperatorIntentPayload(fills), OperatorIntentJson.Options),
                [], "operator", "cli", "local-process", Start));
        }
        internal void Tick() => new OperatorIntentCoordinator(Store)
        {
            ClaudeUsageBackfills = new(() => Repository)
        }.ExecuteWorkspacePending(TickKernel);
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
