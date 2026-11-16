using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each invocation owns its workspace, stores and probe; console capture is async-local.
public sealed class CliReadOnlyVerbWriteTransactionCensusTests : CliTaskQueryTestSupport
{
    private const string GoalIdValue = "abc10000aaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MutationRefusal = "State mutation is not allowed for this test.";

    // Bounded catalog/handler audit, not a completeness partition. Each row names its handler's output.
    // Moved forms have no mutating subcommands: flake-census only reads AcceptanceFailingTestIndex;
    // worker-profiles prints the supplied catalog; operator-commands prints help;
    // backlog-premise-check opens stores read-only and evaluates goals; cross-goal-start-plan only
    // builds/prints a plan. Criterion 2's write-form clause is waived for these purely read-only verbs
    // by the operator's write-form-guard-applicability answer. Sibling writes are guarded below.
    // Excluded: portfolio/portfolio-view, epic-members, epic-suggestions and backlog-triage construct
    // write-mode stores (PortfolioStore/BacklogStore setup can create files).
    // worker-profile-check resolves host executables; goal-recovery/readiness-repair/supervisor can mutate;
    // dogfood-eval has no confirmed read-only handler audit; goal-timing --all is pinned to the writer
    // route by CliGoalTimingReadOnlyRouteTests, outside this slice's file boundary.
    // goal-events calls Directory.CreateDirectory; Program-only verbs are not exercised by this router.
    private static readonly CensusForm[] ReadOnlyForms =
    [
        new(["flake-census"], "Census.Flake | distinct-goals=2"),
        new(["worker-profiles"], "Worker profiles:"),
        new(["operator-commands"], "Usage: operator-commands"),
        new(["backlog-premise-check"], "Backlog premise check:"),
        new(["backlog-premise-check", "--min-age-days", "0"], "min-age-days=0"),
        new(["cross-goal-start-plan"], "Cross-goal subscription start plan:"),
        new(["cross-goal-start-plan", "--confirm-large-paid-subscription-start"], "Cross-goal subscription start plan:"),
        new(["tasks"], "Census task"),
        new(["task", "abc10000"], "Census task"),
        new(["agents"], "Agents:"),
        new(["status", "abc10000"], "Census goal"),
        new(["goals"], "Census goal"),
        new(["goals", "--epic", "Board"], "Census goal"),
        new(["timeline", "abc10000"], "Timeline:"),
        new(["evidence", "abc10000"], "Goal abc10000 evidence"),
        new(["stages", "abc10000"], "Goal abc10000 SDLC stages:"),
        new(["backlog-list"], "Census backlog"),
        new(["backlog-show", "abc10000"], "Census backlog"),
        new(["epic-plan", "Board"], "Epic: Board"),
        new(["operator-inbox"], "Operator inbox:"),
        new(["loop-health"], "Loop health report"),
        new(["help", "operator-commands"], "Usage: operator-commands [--help]"),
        new(["model-functions"], "Model functions"),
        new(["epic-list"], "Board"),
        new(["epic-show", "Board"], "Board"),
        new(["dogfood-log", "list"], "No dogfood log entries")
    ];

    [Fact]
    public void ReadOnlyCensusTable_NoFormEntersWriteTransaction()
    {
        var observations = ReadOnlyForms.Select(Observe).ToArray();
        var report = BuildCensusReport(observations);
        Assert.True(report.Length == 0, report);
    }

    [Fact]
    public void EpicSuggest_SameHarness_EntersWriteTransaction()
    {
        var observation = Observe(new CensusForm(["epic-suggest"], ""));
        AssertWriteTransaction(observation);
        Assert.Contains("epic-suggest entered the state write transaction", BuildCensusReport([observation]));
    }

    [Fact]
    public void CensusReport_NamesEveryOffendingForm()
    {
        var observations = new[]
        {
            Observe(new CensusForm(["epic-suggest"], "")),
            Observe(new CensusForm(["backlog-premise-close", "abc10000", "--confirm-backlog-premise-close"], ""))
        };
        Assert.All(observations, AssertWriteTransaction);
        var report = BuildCensusReport(observations);
        foreach (var observation in observations)
            Assert.Contains($"{observation.Form.Label} entered the state write transaction", report);
    }

    [Fact]
    public void MovedVerbWriteForms_StillEnterWriteTransaction()
    {
        string[][] forms =
        [
            ["backlog-premise-close", "abc10000", "--confirm-backlog-premise-close"],
            ["start-subscription-ready-goals", "--confirm-batch-start"],
            ["worker-profile-import", "profiles.json"]
        ];
        foreach (var args in forms)
        {
            Assert.False(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            Assert.False(CliPersistentStateRunner.SkipsKernelState(args));
            AssertWriteTransaction(Observe(new CensusForm(args, "")));
        }
    }

    [Theory]
    [InlineData("backlog-premise-check")]
    [InlineData("cross-goal-start-plan")]
    public void MovedBoardSnapshotForms_MatchWriterOutput(string verb)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = CreateSeed(workspace);
            string[] args = [verb];
            Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
            var expected = ExecuteWithRealStore(args, workspace, skipReadOnlyRoute: true);
            var actual = ExecuteWithRealStore(args, workspace, skipReadOnlyRoute: false);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Observation Observe(CensusForm form)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            // All setup precedes construction of the independently measured probe.
            var repository = new ProbeStateRepository(CreateSeed(workspace));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Exception? error = null;
            bool? changed = null;
            var output = CaptureConsole(() => error = Record.Exception(() =>
            {
                changed = CliPersistentStateRunner.ExecuteCommand(form.Args, repository, workspace,
                    ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
            }));
            return new Observation(form, repository.MutationAttempts,
                repository.ObservedWriteOperationTags.ToArray(), error, changed, output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string BuildCensusReport(IEnumerable<Observation> observations)
    {
        var failures = new List<string>();
        foreach (var observation in observations)
        {
            if (observation.MutationAttempts > 0)
                failures.Add($"{observation.Form.Label} entered the state write transaction (TransactWithOutboxAsync).");
            if (observation.Error is not null)
                failures.Add($"{observation.Form.Label} incomplete: {observation.Error.GetType().Name}: {observation.Error.Message}");
            else if (observation.Changed != false ||
                     !observation.Output.Contains(observation.Form.ExpectedOutput, StringComparison.Ordinal))
                failures.Add($"{observation.Form.Label} incomplete: changed={observation.Changed}; expected output '{observation.Form.ExpectedOutput}'; actual: {observation.Output}");
        }
        return string.Join(Environment.NewLine, failures);
    }

    private static void AssertWriteTransaction(Observation observation)
    {
        // The probe implements IOrchestratorStateOutboxRepository, so this runner's fall-through
        // enters TransactWithOutboxAsync rather than TransactAsync, even when the handler saves nothing.
        Assert.Equal(1, observation.MutationAttempts);
        Assert.Equal(MutationRefusal, Assert.IsType<InvalidOperationException>(observation.Error).Message);
        Assert.Contains($"cli:{observation.Form.Args[0]}", observation.WriteOperationTags);
    }

    private static string ExecuteWithRealStore(string[] args, OrchestratorWorkspace workspace, bool skipReadOnlyRoute)
    {
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.False(CliPersistentStateRunner.ExecuteCommand(args, repository,
            workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal,
            skipReadOnlyRoute: skipReadOnlyRoute)));
    }

    private static AgentOrchestratorKernel CreateSeed(OrchestratorWorkspace workspace)
    {
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var portfolio = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = portfolio.AddEpicAsync("Board").GetAwaiter().GetResult();
        portfolio.AssignGoalToEpicAsync(GoalIdValue, epic.Id).GetAwaiter().GetResult();
        _ = new BacklogStore(workspace.BacklogStorePath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workspace.BacklogStorePath,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO backlog (id, title, body, status, created_at, updated_at, source_goal_id)
                VALUES ('abc10000eeeeeeeeeeeeeeeeeeeeeeee', 'Census backlog', '', 'Open',
                        '2026-10-07T00:00:00Z', '2026-10-07T00:00:00Z', NULL);
                """;
            command.ExecuteNonQuery();
        }
        _ = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var recordedAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        new AcceptanceFailingTestIndex(Path.Combine(workspace.OrchestratorDirectory,
            "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName)).Append(
            [Failure("goal-a"), Failure("goal-b")], recordedAt);

        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId(GoalIdValue), "Census goal",
            [new TaskSpec(new TaskId("abc10000dddddddddddddddddddddddd"), "Census task", AgentRole.Developer)]);
        // Independent read models (epic reports) read the real store, while router entry is probed.
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
        return kernel;

        AcceptanceFailingTestIndexRecord Failure(string goalId) => new(
            AcceptanceFailingTestIndex.ContractVersion, AcceptanceFailingTestIndexKinds.GateFailure,
            goalId, recordedAt, TestIdentity: "Census.Flake", InsideChangedPaths: false);
    }

    private sealed record CensusForm(string[] Args, string ExpectedOutput)
    {
        public string Label => string.Join(" ", Args);
    }

    private sealed record Observation(CensusForm Form, int MutationAttempts,
        IReadOnlyList<string?> WriteOperationTags, Exception? Error, bool? Changed, string Output);
}
