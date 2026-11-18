using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

// Parallel-safe: each case owns its databases and children; child environments are allowlisted.
public sealed class CliCommandTestsStartDispatchesPaidRetryCheckpoint : CliCommandTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedBatch_PaidRetry_PreservesBothProcessesAndDurableBaseline(bool positional)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scenario = CreateScenario(mixed: true);
        await scenario.Repository.SaveAsync(scenario.Kernel);

        var result = RunBatch(scenario, positional);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.DoesNotContain("requires a durable process checkpoint", result.Stderr);
        var restored = await scenario.Repository.LoadAsync();
        var goal = restored.GetGoal(scenario.Goal.Id);
        Assert.Equal(2, goal.Tasks.Count);
        foreach (var task in goal.Tasks)
        {
            var process = Assert.IsType<TaskProcessRecord>(task.LastProcess);
            Assert.NotNull(process.ProcessIdentityStartedAt);
            Assert.False(string.IsNullOrWhiteSpace(process.ProcessIdentityImagePath));
            Assert.Single(goal.Timeline, entry =>
                entry.TaskId == task.Id && entry.Kind == ProgressKind.TaskProcessStarted);
            Assert.Single(scenario.ReadOwnedIdentities(), identity =>
                identity.ProcessId == process.ProcessId &&
                identity.StartedAt == process.ProcessIdentityStartedAt &&
                identity.ImagePath == process.ProcessIdentityImagePath);
        }
        var paid = restored.GetTask(goal.Id, scenario.PaidTask.Id);
        Assert.Equal(PaidRouteClassification.Paid, paid.LastDispatch!.PaidRoute);
        var admission = Assert.Single(paid.RetryAdmissionHistory, receipt =>
            receipt.LinkedDispatchAt == paid.LastDispatch.DispatchedAt);
        Assert.NotNull(admission.WorkerStartClaimedAt);
        Assert.NotNull(admission.WorkerStartedAt);
        Assert.Equal(PaidRouteClassification.NonPaid,
            restored.GetTask(goal.Id, scenario.OrdinaryTask!.Id).LastDispatch!.PaidRoute);
        Assert.Equal(new TaskId?[] { scenario.OrdinaryTask.Id, paid.Id }, goal.Timeline
            .Where(entry => entry.Kind == ProgressKind.TaskProcessStarted).Select(entry => entry.TaskId));
        Assert.Contains(goal.Timeline, entry =>
            entry.TaskId == paid.Id && entry.Kind == ProgressKind.TaskNote &&
            entry.Message == Scenario.DurableNote);
    }

    [Fact]
    public async Task PaidRetry_CriticalPersistFails_DoesNotLaunchOrChangePersistedTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scenario = CreateScenario(mixed: false);
        await scenario.Repository.SaveAsync(scenario.Kernel);
        var before = JsonSerializer.Serialize(scenario.Kernel.ExportGoalSnapshot(scenario.Goal.Id).Tasks.Single());
        scenario.ExecuteSql("""
            CREATE TRIGGER fail_critical_insert BEFORE INSERT ON goals
            BEGIN SELECT RAISE(ABORT, 'forced critical checkpoint failure'); END;
            CREATE TRIGGER fail_critical_update BEFORE UPDATE ON goals
            BEGIN SELECT RAISE(ABORT, 'forced critical checkpoint failure'); END;
            """);

        var result = RunBatch(scenario);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("forced critical checkpoint failure", result.Stderr);
        Assert.DoesNotContain("requires a durable process checkpoint", result.Stderr);
        scenario.ExecuteSql("DROP TRIGGER fail_critical_insert; DROP TRIGGER fail_critical_update;");
        var restored = await scenario.Repository.LoadAsync();
        Assert.Equal(before, JsonSerializer.Serialize(restored.ExportGoalSnapshot(scenario.Goal.Id).Tasks.Single()));
        Assert.Null(restored.GetTask(scenario.Goal.Id, scenario.PaidTask.Id).LastProcess);
        Assert.Empty(scenario.ReadOwnedIdentities());
        // The only task is paid. Neither a host nor its release gate was even prepared.
        Assert.Empty(scenario.FindLogs("*.dispatch.json"));
        Assert.Empty(scenario.FindLogs("*.start-gate"));
        Assert.False(File.Exists(scenario.PaidLaunchMarker));
    }

    [Fact]
    public async Task OrdinaryBatch_PendingIntent_PreservesDenialAndUsesAdmissionLookup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scenario = CreateScenario(mixed: true, ordinaryOnly: true);
        await scenario.Repository.SaveAsync(scenario.Kernel);
        var store = SqliteOperatorIntentStore.ForDirectories(
            scenario.Workspace.OrchestratorDirectory, scenario.Workspace.LogDirectory);
        var task = scenario.OrdinaryTask!;
        var intent = await store.EnqueueAsync(new OperatorIntentRecord(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Progress,
            scenario.Goal.Id.Value, task.Id.Value, "{}", [], "operator", "cli", "local-process", DateTimeOffset.UtcNow));

        var result = RunBatch(scenario);

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("StartDispatches: ready=1", result.Stdout);
        Assert.Contains("changed=0", result.Stdout);
        Assert.DoesNotContain("running pid=", result.Stdout);
        var restored = await scenario.Repository.LoadAsync();
        Assert.Null(restored.GetTask(scenario.Goal.Id, task.Id).LastProcess);
        Assert.Empty(scenario.ReadOwnedIdentities());
        // Refresh binds a new prepared timestamp. Read that durable binding rather than the seed.
        using var connection = scenario.OpenConnection(Path.Combine(
            scenario.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName));
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dispatched_at, task_version FROM task_intent_preparations WHERE goal_id=$goal AND task_id=$task ORDER BY dispatched_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$goal", scenario.Goal.Id.Value);
        command.Parameters.AddWithValue("$task", task.Id.Value);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "The CLI did not bind a prepared admission.");
        var at = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        var version = reader.GetInt64(1);
        var suppression = store.GetSuppression($"prepared:{scenario.Goal.Id.Value}:{task.Id.Value}:{at:O}:{version}");
        Assert.NotNull(suppression);
        Assert.Equal(intent.Id, suppression.IntentId);
        Assert.Equal("Pending", suppression.IntentStatus);
        Assert.Equal(task.Id.Value, suppression.TaskId);
    }

    private Scenario CreateScenario(bool mixed, bool ordinaryOnly = false)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var ordinaryAgent = new AgentDefinition(new AgentId("ordinary"), "Ordinary", AgentRole.Ideation,
            new ModelProfile("Ollama", "qwen3:8b", ModelCapability.Text, SubscriptionMode.LocalBridge),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("ordinary-fixture", "qwen3:8b"),
            IsProviderRoutingConstrained: true);
        var paidAgent = new AgentDefinition(new AgentId("paid"), "Paid", AgentRole.Planner,
            new ModelProfile("OpenAI", "gpt-6.1-sol", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("paid-retry-fixture", "gpt-6.1-sol", "medium"),
            IsProviderRoutingConstrained: true);
        var agents = new[] { ordinaryAgent, paidAgent };
        AgentCatalogStore.Save(workspace.AgentCatalogPath, new AgentCatalog(agents));
        var paidMarker = Path.Combine(root, "paid-started.txt");
        var paidCommand = "[IO.File]::WriteAllText('" + paidMarker.Replace("'", "''") + "', 'started')";
        var profiles = new WorkerProfileCatalog([
            new WorkerProfile("ordinary-fixture", "Write-Output ordinary"),
            new WorkerProfile("paid-retry-fixture", paidCommand)]);
        WorkerProfileStore.Save(workspace.WorkerProfilePath, profiles);
        var kernel = new AgentOrchestratorKernel();
        // Independent starts: a live Researcher before the Planner would require a completed research artifact.
        var ordinary = mixed ? new TaskSpec(TaskId.New(), "Explore ordinary fixture", AgentRole.Ideation) : null;
        var paid = ordinaryOnly ? null : new TaskSpec(TaskId.New(), "Plan a paid retry", AgentRole.Planner);
        var tasks = new List<TaskSpec>();
        if (ordinary is not null) tasks.Add(ordinary);
        if (paid is not null) tasks.Add(paid);
        var goal = kernel.CreateGoal("Start paid retry with durable checkpoints", tasks);
        MarkGoalRefined(kernel, goal);
        kernel.ActivateGoal(goal.Id, agents);
        if (paid is not null)
        {
            kernel.RetryTask(goal.Id, paid.Id, "Plan after a source finding", RetryCause.NewSourceFinding);
            kernel.RecordTaskNote(goal.Id, paid.Id, Scenario.DurableNote);
            var fingerprint = RetryContextFingerprintBuilder.Build(new RetryContextFingerprintInput(
                goal.Id.Value, paid.Id.Value, paid.RequiredRole, "OpenAI", "gpt-6.1-sol",
                PaidRouteClassification.Paid, "candidate", "criteria", [], [], [], [], "base", "main"));
            kernel.RecordTaskDispatch(goal.Id, paid.Id, new TaskDispatchRecord(
                "paid-retry-fixture", paidCommand, root, DateTimeOffset.UtcNow,
                ProviderName: "OpenAI", ModelName: "gpt-6.1-sol", AssignedAgentId: paidAgent.Id.Value,
                RetryContextFingerprint: fingerprint, PaidRoute: PaidRouteClassification.Paid));
        }
        // Planner retry invalidates downstream Ideation dispatches; prepare the ordinary start afterwards.
        if (ordinary is not null)
            kernel.RecordTaskDispatch(goal.Id, ordinary.Id, new TaskDispatchRecord(
                "ordinary-fixture", "Write-Output ordinary", root, DateTimeOffset.UtcNow,
                AssignedAgentId: ordinaryAgent.Id.Value, PaidRoute: PaidRouteClassification.NonPaid));
        // Exercise the same preparation the CLI repeats, and fail loudly if it loses the paid route.
        new GoalDispatchOperations().RefreshPreparedDispatchesBeforeStart(kernel, workspace, goal, agents, profiles);
        if (paid is not null)
        {
            Assert.Equal(PaidRouteClassification.Paid, kernel.GetTask(goal.Id, paid.Id).LastDispatch!.PaidRoute);
            Assert.NotNull(kernel.GetTask(goal.Id, paid.Id).LastDispatch!.RetryContextFingerprint);
        }
        if (ordinary is not null)
            Assert.Equal(PaidRouteClassification.NonPaid, kernel.GetTask(goal.Id, ordinary.Id).LastDispatch!.PaidRoute);
        return new Scenario(root, workspace, kernel, goal, ordinary, paid, paidMarker);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunBatch(Scenario scenario, bool positional = false)
    {
        string[] arguments = positional
            ? ["start-dispatches", scenario.Goal.Id.Value[..8], "--confirm-batch-start", "--confirm-large-paid-subscription-start"]
            : ["start-dispatches", "--goal", scenario.Goal.Id.Value[..8], "--confirm-batch-start", "--confirm-large-paid-subscription-start"];
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = scenario.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = scenario.Root;
        start.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = Path.Combine(scenario.Root, ".orchestrator", "test-dotnet");
        start.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return CliAppCommandTimeoutReport.Run(start, arguments, 120000);
    }

    private sealed class Scenario(string root, OrchestratorWorkspace workspace, AgentOrchestratorKernel kernel,
        Goal goal, TaskSpec? ordinaryTask, TaskSpec? paidTask, string paidLaunchMarker) : IDisposable
    {
        internal const string DurableNote = "Keep this durable note through reservation and start checkpoints.";
        internal string Root => root;
        internal OrchestratorWorkspace Workspace => workspace;
        internal AgentOrchestratorKernel Kernel => kernel;
        internal Goal Goal => goal;
        internal TaskSpec? OrdinaryTask => ordinaryTask;
        internal TaskSpec PaidTask => paidTask ?? throw new InvalidOperationException("This fixture has no paid task.");
        internal string PaidLaunchMarker => paidLaunchMarker;
        internal SqliteOrchestratorStateRepository Repository { get; } = new(workspace.SqliteStatePath);

        internal string[] FindLogs(string pattern) => Directory.Exists(workspace.LogDirectory)
            ? Directory.GetFiles(workspace.LogDirectory, pattern, SearchOption.AllDirectories) : [];

        internal SqliteConnection OpenConnection(string? databasePath = null)
        {
            var connection = new SqliteConnection($"Data Source={databasePath ?? workspace.SqliteStatePath};Pooling=False");
            connection.Open();
            return connection;
        }

        internal void ExecuteSql(string sql)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal IReadOnlyList<SpawnProcessIdentity> ReadOwnedIdentities()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT process_id, process_started_at, image_path FROM spawn_registry";
            using var reader = command.ExecuteReader();
            var identities = new List<SpawnProcessIdentity>();
            while (reader.Read()) identities.Add(new SpawnProcessIdentity(reader.GetInt32(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2)));
            return identities;
        }

        public void Dispose()
        {
            // The isolated registry includes starts that failed before LastProcess could be saved.
            try
            {
                foreach (var identity in ReadOwnedIdentities())
                {
                    try
                    {
                        using var process = Process.GetProcessById(identity.ProcessId);
                        try
                        {
                            // The dispatch host owns the job that reaps its fixture worker on host exit.
                            TestOwnedProcessStop.StopRootIfSame(process, identity, TimeSpan.FromSeconds(30));
                        }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    catch (ArgumentException) { } // The exact owned process already exited.
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }
    }
}
