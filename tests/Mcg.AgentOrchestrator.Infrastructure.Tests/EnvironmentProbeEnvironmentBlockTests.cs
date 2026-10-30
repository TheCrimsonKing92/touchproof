using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

public sealed class EnvironmentProbeEnvironmentBlockTests
{
    [Fact]
    public async Task ProbeAndDispatchHost_ReceiveTheSameExplicitEnvironmentBlock()
    {
        var environment = EnvironmentContractTestFixture.Environment("sentinel-worker-path");
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        await runner.RunAsync(EnvironmentContractTestFixture.Contract.Tools[0], environment, Path.GetTempPath());
        var startInfo = DispatchHostStartInfoFactory.Create(Path.GetTempPath(), "host.dll", "parameters.json", "start-gate", environment);
        Assert.Equal("sentinel-worker-path", runner.ObservedEnvironment!.Variables["PATH"]);
        Assert.Equal(runner.ObservedEnvironment.Variables["PATH"], startInfo.Environment["PATH"]);
        Assert.All(environment.Variables, pair => Assert.Equal(pair.Value, startInfo.Environment[pair.Key]));
        Assert.False(startInfo.UseShellExecute);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdmissionAndFinalWorker_UseTheSameSandboxToolEnvironment(bool lowIntegrity)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcg-probe-final-environment", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var windowsApps = Path.Combine(root, "profile", "AppData", "Local", "Microsoft", "WindowsApps");
            var toolPath = Path.Combine(root, "tools");
            var inherited = new ConductorChildEnvironment(new Dictionary<string, string?>
            {
                ["PATH"] = string.Join(Path.PathSeparator, windowsApps, toolPath),
                ["TEMP"] = Path.Combine(root, "inherited-temp"),
                ["TMP"] = Path.Combine(root, "inherited-tmp"),
                ["GOTOOLCHAIN"] = "auto"
            });
            var (kernel, goal) = SimpleGoal();
            var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
            var starts = 0;
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
            driver.EnvironmentGate = new(kernel, "project", root, () => EnvironmentContractTestFixture.Contract,
                runner, _ => inherited, workerEnvironments: (_, _) =>
                    [WorkerEnvironmentProbeContexts.Create(inherited, root, lowIntegrity, disableSharedCompilation: true)]);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(1, starts);
            var observed = runner.ObservedEnvironment!.Variables;
            var worker = WorkerProcessRunner.BuildPowerShellStartInfo("unused", root);
            inherited.Apply(worker);
            var parameters = new DispatchProcessHost.DispatchRunParameters("unused", root,
                Path.Combine(root, "out.log"), Path.Combine(root, "err.log"), Path.Combine(root, "exit.txt"), null,
                DisableSharedCompilation: true, SandboxLowIntegrity: lowIntegrity,
                SandboxWorktreeWritable: false);
            DispatchProcessHost.ApplyWorkerSandbox(worker, parameters,
                new WorkerSandboxPreparer(new LowIntegrityLabeler()),
                protectWorkspaceBoundary: _ => { }, protectGitMetadata: _ => { });

            foreach (var name in new[] { "PATH", "TEMP", "TMP", "PSModuleAnalysisCachePath", "GOPATH", "GOMODCACHE",
                         "GOCACHE", "GOTOOLCHAIN", "GOENV", "GOFLAGS" })
                Assert.Equal(worker.Environment[name], observed[name]);
            Assert.Equal("local", observed["GOTOOLCHAIN"]);
            if (lowIntegrity)
            {
                Assert.NotEqual(inherited.Variables["PATH"], observed["PATH"]);
                Assert.DoesNotContain(windowsApps, observed["PATH"]!);
                Assert.StartsWith(Path.Combine(root, ".mcg-sandbox", "bin"), observed["PATH"]!);
                Assert.Equal(Path.Combine(root, ".mcg-sandbox", "temp"), observed["TEMP"]);
            }
            else
            {
                Assert.Equal(inherited.Variables["PATH"], observed["PATH"]);
                Assert.Equal(inherited.Variables["TEMP"], observed["TEMP"]);
            }
            Assert.Equal(string.Join(Path.PathSeparator, windowsApps, toolPath), inherited.Variables["PATH"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ToolOnlyOnRemovedWindowsAppsPath_HoldsUntilWorkerPathContainsIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "mcg-filtered-probe", Guid.NewGuid().ToString("N"));
        var windowsApps = Path.Combine(directory, "Microsoft", "WindowsApps");
        var inherited = EnvironmentContractTestFixture.Environment(windowsApps);
        var runner = new ToolPathRunner(windowsApps);
        var (kernel, goal) = SimpleGoal();
        var starts = 0;
        var events = new List<string>();
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
        driver.EnvironmentGate = new(kernel, "project", directory, () => EnvironmentContractTestFixture.Contract,
            runner, _ => inherited, (kind, _, _) => events.Add(kind), workerEnvironments: (_, _) =>
                [WorkerEnvironmentProbeContexts.Create(inherited, directory, true, true)]);

        driver.EnvironmentGate.ProbeAtStart([goal]);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(0, starts);
        Assert.NotNull(EnvironmentHold.TryRead(goal));
        Assert.Single(events, kind => kind == "environment-hold");
        Assert.DoesNotContain(windowsApps, runner.Path!);

        runner.RequiredDirectory = Path.Combine(directory, "restored-tool");
        inherited = EnvironmentContractTestFixture.Environment(runner.RequiredDirectory);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(1, starts);
        Assert.Null(EnvironmentHold.TryRead(goal));
        Assert.Single(events, kind => kind == "environment-hold-cleared");
    }

    [Fact]
    public void PreparedDispatch_ProbesItsOwnDirectoryAndSandboxMode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "mcg-probe-prepared", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "goal-worktree");
        var workspace = new OrchestratorWorkspace(root, root, Path.Combine(root, ".orchestrator"),
            "default", false, "default", false, "unused", "unused", "unused", "unused", "unused", "unused", "unused");
        var (kernel, goal) = SimpleGoal();
        kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id,
            new TaskDispatchRecord("codex-cli", "unused", directory, DateTimeOffset.UnixEpoch,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var inherited = EnvironmentContractTestFixture.Environment(Path.Combine(root, "Microsoft", "WindowsApps"));

        var context = Assert.Single(WorkerEnvironmentProbeContexts.Capture(workspace, goal, DefaultAgents(),
            WorkerProfileCatalog.Default(), ConductorAutonomyPolicy.Conservative, inherited,
            new WorkerSandboxOptions(true, "unused", "unused")));

        Assert.Equal(directory, context.WorkingDirectory);
        Assert.StartsWith(Path.Combine(directory, ".mcg-sandbox", "bin"), context.Environment.Variables["PATH"]!);
        Assert.DoesNotContain("WindowsApps", context.Environment.Variables["PATH"]!);
        Assert.Equal(Path.Combine(directory, ".mcg-sandbox", "temp"), context.Environment.Variables["TEMP"]);
        Assert.Equal("0", context.Environment.Variables["DOTNET_CLI_USE_MSBUILD_SERVER"]);
    }

    private sealed class LowIntegrityLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }

    private sealed class ToolPathRunner(string requiredDirectory) : IEnvironmentProbeRunner
    {
        internal string RequiredDirectory { get; set; } = requiredDirectory;
        internal string? Path { get; private set; }
        public Task<EnvironmentProbeResult> RunAsync(EnvironmentToolRequirement tool, ConductorChildEnvironment environment,
            string workingDirectory, CancellationToken cancellationToken = default)
        {
            Path = environment.Variables["PATH"];
            var pass = Path!.Split(System.IO.Path.PathSeparator).Contains(RequiredDirectory);
            return Task.FromResult(new EnvironmentProbeResult(tool, pass, pass ? 0 : 7,
                pass ? "available" : "not on worker PATH", pass ? "pass" : "failed"));
        }
    }
}
