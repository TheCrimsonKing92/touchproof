using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Owns its SQLite workspace; follows the CLI collection for process-global console capture.
[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CostGoalOutcomeMetricGoalVerbTests : CliCommandTestBase
{
    private protected override bool UsesGit => false;
    private const string CostBrief = "Reduce selection work.\nExpected effect: cheaper gate selection.\n";
    private const string MetricSection = "\n## Outcome metric\nmetric: selection-milliseconds; direction: decrease; target: 20%; window: 5 gates\n";

    [Theory]
    [InlineData("--brief-file")]
    [InlineData("--text-file")]
    public async Task Filing_MissingMetric_RefusesWithoutCreatingGoal(string fileFlag)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var result = Execute(workspace, repository, CostBrief, fileFlag);

        Assert.NotNull(result.Error);
        Assert.Contains("cost-goal-outcome-metric", result.Error.Message);
        Assert.Contains("no well-formed ## Outcome metric line", result.Error.Message);
        Assert.Null(result.Goal);
        Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Theory]
    [InlineData("--brief-file", true)]
    [InlineData("--brief-file", false)]
    [InlineData("--text-file", true)]
    [InlineData("--text-file", false)]
    public async Task Filing_ValidCostOrOrdinaryBrief_CreatesGoal(string fileFlag, bool cost)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var brief = cost ? CostBrief + MetricSection : "Implement an ordinary filing change.";
        var result = Execute(workspace, repository, brief, fileFlag);

        Assert.Null(result.Error);
        Assert.NotNull(result.Goal);
        Assert.Equal(brief.Trim(), Assert.Single((await repository.LoadAsync()).Goals).Objective);
        Assert.DoesNotContain("cost-goal-outcome-metric-override", ReadLog(workspace));
    }

    [Fact]
    public async Task Filing_GatesRecordedAfterFiling_DoNotChangeStoredBaseline()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var source = await backlog.AddAsync("Reduce cost", "Measure gate outcomes");
        var past = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WriteGate(workspace, past, 1, 40, 4, null);
        WriteGate(workspace, past.AddDays(1), 2, 60, 6, "Full gate", passed: false);
        WriteGate(workspace, past.AddDays(2), 3, 90, 9, null);
        WriteGate(workspace, past.AddDays(3), 4, 30, 3, "Full gate");
        WriteGate(workspace, past.AddDays(4), 5, 600, 60, null, completed: false);
        WriteGate(workspace, past.AddYears(75), 6, 900, 90, null);
        var brief = CostBrief + """

            ## Outcome metric
            metric: selection-milliseconds; direction: decrease; target: 20%; window: 3 gates
            metric: executed-class-count; direction: increase; target: 6; window: 3 gates
            metric: full-gate-rate; direction: decrease; target: .4; window: 3 gates
            metric: gate-test-cpu-seconds; direction: decrease; target: 5%; window: 5 gates
            metric: process-launches; direction: decrease; target: 5; window: 8 gates
            """;

        var result = Execute(workspace, repository, brief, "--brief-file",
            "--backlog-item", source.Id, "--backlog-coverage", "full");

        Assert.Null(result.Error);
        var persistedGoal = Assert.Single((await repository.LoadAsync()).Goals);
        var filing = new CostGoalOutcomeStore(workspace.OrchestratorDirectory).Read(persistedGoal.Id)!.Filing;
        Assert.Equal(source.Id, filing.SourceBacklogItemId);
        Assert.Collection(filing.Baselines,
            row => Assert.Equal(new(new("selection-milliseconds", "decrease", "20%", 3), 60, 3, null), row),
            row => Assert.Equal(new(new("executed-class-count", "increase", "6", 3), 6, 3, null), row),
            row => Assert.Equal(new(new("full-gate-rate", "decrease", ".4", 3), 2d / 3, 3, null), row),
            row => Assert.Equal(new(new("gate-test-cpu-seconds", "decrease", "5%", 5), null, 0, "no-gate-source"), row),
            row => Assert.Equal(new(new("process-launches", "decrease", "5", 8), null, 0, "no-gate-source"), row));
    }

    private static void WriteGate(OrchestratorWorkspace workspace, DateTimeOffset recordedAt, int serial,
        double selectionMilliseconds, int classCount, string? fullGateReason, bool passed = true, bool completed = true)
    {
        var attemptId = FormattableString.Invariant($"fixture-gate-{recordedAt:yyyyMMddHHmmssfff}-{serial:x32}");
        var directory = Directory.CreateDirectory(Path.Combine(workspace.OrchestratorDirectory,
            "acceptance-gate-attempts", serial % 2 == 0 ? "solo" : "train"));
        var prefix = Path.Combine(directory.FullName, attemptId);
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, attemptId, ["test-lane"], [])
        {
            SelectedExecution = CostGoalOutcomeBaselineTests.Selection(selectionMilliseconds, classCount, fullGateReason)
        };
        File.WriteAllText(prefix + AcceptanceGateLanePlan.ArtifactSuffix, plan.ToJson());
        if (completed)
        {
            var verification = new AcceptanceVerificationResult(passed, false, passed ? 0 : 1, null,
                TestResultPaths: [prefix + ".test-lane.trx"]);
            AcceptanceAttemptResultRecord.Write(prefix + ".result.json",
                AcceptanceAttemptResultRecord.ForVerification(verification, null, null));
        }
    }

    [Fact]
    public async Task Filing_CostTaggedSourceWithoutTextSignal_Refuses()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Cost work", "Reduce work");
        await store.UpdateAsync(item.Id, new BacklogItemUpdate(Tags: "cost"));
        var result = Execute(workspace, repository, "Reduce selection work.", "--brief-file",
            "--backlog-item", item.Id, "--backlog-coverage", "full");

        Assert.NotNull(result.Error);
        Assert.Contains("cost-goal-outcome-metric", result.Error.Message);
        Assert.Null(result.Goal);
        Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Theory]
    [InlineData("--brief-file", false)]
    [InlineData("--brief-file", true)]
    [InlineData("--text-file", false)]
    [InlineData("--text-file", true)]
    public async Task Filing_OverrideReason_CreatesGoalAndRecordsAudit(string fileFlag, bool inlineReason)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        string[] flags = inlineReason
            ? ["--override-draft-checks=reviewed measured exception"]
            : ["--override-draft-checks", "reviewed measured exception"];
        var result = Execute(workspace, repository, CostBrief, fileFlag, flags);

        Assert.Null(result.Error);
        Assert.NotNull(result.Goal);
        Assert.Single((await repository.LoadAsync()).Goals);
        var log = ReadLog(workspace);
        Assert.Contains("cost-goal-outcome-metric-override", log);
        Assert.Contains("reviewed measured exception", log);
        Assert.Contains("failedChecks=cost-goal-outcome-metric", log);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("next-flag")]
    [InlineData("inline-empty")]
    public async Task Filing_OverrideWithoutReason_Refuses(string scenario)
    {
        string[] flags = scenario switch
        {
            "missing" => ["--override-draft-checks"],
            "empty" => ["--override-draft-checks", ""],
            "whitespace" => ["--override-draft-checks", "   "],
            "next-flag" => ["--override-draft-checks", "--pipeline", "auto"],
            _ => ["--override-draft-checks="]
        };
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var result = Execute(workspace, repository, CostBrief, "--brief-file", flags);

        Assert.NotNull(result.Error);
        Assert.Contains("--override-draft-checks requires a nonblank reason", result.Error.Message);
        Assert.Null(result.Goal);
        Assert.Empty((await repository.LoadAsync()).Goals);
        Assert.DoesNotContain("cost-goal-outcome-metric-override", ReadLog(workspace));
    }

    [Fact]
    public async Task Filing_OverrideAuditWriteFails_CreatesNoGoal()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        Directory.CreateDirectory(workspace.ConductEventsLogPath);
        var result = Execute(workspace, repository, CostBrief, "--brief-file",
            "--override-draft-checks", "reviewed exception");

        Assert.NotNull(result.Error);
        Assert.Null(result.Goal);
        Assert.Empty((await repository.LoadAsync()).Goals);
    }

    [Fact]
    public async Task Filing_ValidBriefWithOverride_CreatesWithoutBypassEvent()
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        var result = Execute(workspace, repository, CostBrief + MetricSection, "--text-file",
            "--override-draft-checks=reviewed exception");

        Assert.Null(result.Error);
        Assert.Single((await repository.LoadAsync()).Goals);
        Assert.DoesNotContain("cost-goal-outcome-metric-override", ReadLog(workspace));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--simple")]
    [InlineData("--run")]
    [InlineData("--from-backlog")]
    public async Task Arguments_OverrideOutsidePlainFileMode_Refuses(string? mode)
    {
        var workspace = CreateWorkspace();
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(new AgentOrchestratorKernel());
        string[] flags = mode is null
            ? ["--override-draft-checks", "reason"]
            : [mode, "--override-draft-checks", "reason"];
        var result = Execute(workspace, repository, CostBrief, mode is null ? null : "--brief-file", flags);

        var error = Assert.IsType<ArgumentException>(result.Error);
        Assert.Contains("--override-draft-checks requires plain goal --brief-file or goal --text-file", error.Message);
        Assert.Null(result.Goal);
        Assert.Empty((await repository.LoadAsync()).Goals);
        Assert.DoesNotContain("cost-goal-outcome-metric-override", ReadLog(workspace));
    }

    private (Goal? Goal, Exception? Error) Execute(OrchestratorWorkspace workspace,
        SqliteOrchestratorStateRepository repository, string brief, string? fileFlag, params string[] flags)
    {
        var path = Path.Combine(workspace.ExecutionDirectory, "brief.md");
        File.WriteAllText(path, brief);
        string[] parts = fileFlag is null ? ["goal", brief, .. flags] : ["goal", fileFlag, path, .. flags];
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([new FixtureRefinerProvider()]);
        Goal? goal = null;
        Exception? error = null;
        CaptureConsoleError(() => CaptureConsole(() => error = Record.Exception(() =>
            CliPersistentStateRunner.ExecuteCommand(parts, repository, workspace,
                ref agents, providers, ref profiles, ref goal))));
        return (goal, error);
    }

    private OrchestratorWorkspace CreateWorkspace()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("fixture-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        return workspace;
    }

    private static string ReadLog(OrchestratorWorkspace workspace) =>
        File.Exists(workspace.ConductEventsLogPath) ? File.ReadAllText(workspace.ConductEventsLogPath) : "";

    private sealed class FixtureRefinerProvider : IModelProvider
    {
        public string ProviderName => "fixture-refiner";
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelResponse("""
                {"behavioralContract":"File the requested goal.","acceptanceCriteria":["The goal is filed."],
                 "verificationClass":"TestVerifiable","decisions":[],"forks":[]}
                """, new ModelUsage(1, 1), "stop"));
    }
}
