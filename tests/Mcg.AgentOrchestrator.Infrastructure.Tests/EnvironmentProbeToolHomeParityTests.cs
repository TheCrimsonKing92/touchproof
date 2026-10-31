using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: all manifest and sandbox files belong to this fixture; no ambient variables change.
public sealed class EnvironmentProbeToolHomeParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-probe-tool-homes", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GateProbe_DeclaredToolHomes_MatchGateProcess(bool projectScoped)
    {
        var (workspace, homes) = CreateWorkspace(projectScoped);
        var (kernel, goal) = SimpleGoal();
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        var gate = new EnvironmentContractGate(kernel, "project", _root,
            () => EnvironmentContractTestFixture.Contract, runner,
            projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);

        Assert.Null(gate.Check(goal, ConductorAutonomyPolicy.Conservative, GoalLifecycleState.Verifying, forGate: true));

        var settings = AcceptanceGateEngineSettings.Load(_root, workspace.ProjectHomeDirectoryOrNull);
        Assert.Equal(homes, settings.DeclaredToolHomes);
        var process = AcceptanceShellProcessStartInfo.ForCheck(["cargo", "--version"], _root,
            null, null, false, new AcceptanceProcessRequest
            {
                EngineSettings = settings,
                GateInvocationId = "tool-home-parity",
                ApparatusReceiptPath = Path.Combine(_root, "apparatus.json")
            }, out _);
        Assert.Equal(1, runner.Calls);
        var observed = runner.ObservedEnvironment!.Variables;
        foreach (var home in homes)
        {
            Assert.Equal(home.Directory, observed[home.Variable]);
            Assert.Equal(process.Environment[home.Variable], observed[home.Variable]);
        }
        Assert.Equal(process.Environment["PATH"], observed["PATH"]);
        Assert.Equal(process.Environment["USERPROFILE"], observed["USERPROFILE"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WorkerProbe_DeclaredToolHomes_MatchFinalWorker(bool projectScoped, bool lowIntegrity)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (workspace, homes) = CreateWorkspace(projectScoped);
        var toolPath = Path.Combine(_root, "tools");
        Directory.CreateDirectory(toolPath);
        foreach (var tool in new[] { "git", "dotnet" })
            File.WriteAllText(Path.Combine(toolPath, tool + ".exe"), string.Empty);
        var inherited = new ConductorChildEnvironment(new Dictionary<string, string?>
        {
            ["PATH"] = toolPath,
            ["RUSTUP_HOME"] = "inherited-rustup-home"
        });
        var (kernel, goal) = SimpleGoal();
        kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id,
            new TaskDispatchRecord("codex-cli", "unused", _root, DateTimeOffset.UnixEpoch,
                WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var runner = new EnvironmentContractTestFixture.Runner { Pass = true };
        var gate = new EnvironmentContractGate(kernel, "project", _root,
            () => EnvironmentContractTestFixture.Contract, runner,
            workerEnvironments: (member, policy) => WorkerEnvironmentProbeContexts.Capture(
                workspace, member, DefaultAgents(), WorkerProfileCatalog.Default(), policy, inherited,
                new WorkerSandboxOptions(lowIntegrity, "unused", "unused")));

        Assert.Null(gate.Check(goal, ConductorAutonomyPolicy.Conservative, GoalLifecycleState.Verifying));

        // Match BackgroundDispatchRunner's declared-home binding before final sandbox preparation.
        var host = DispatchHostStartInfoFactory.Create(_root, "host.dll", "parameters.json", "start-gate", inherited);
        var settings = AcceptanceGateEngineSettings.Load(workspace.RootDirectory, workspace.ProjectHomeDirectoryOrNull);
        Assert.Equal(homes, settings.DeclaredToolHomes);
        DeclaredToolHomeEnvironment.Apply(host.Environment, settings.DeclaredToolHomes);
        var worker = WorkerProcessRunner.BuildPowerShellStartInfo("unused", _root);
        new ConductorChildEnvironment(host.Environment).Apply(worker);
        var parameters = new DispatchProcessHost.DispatchRunParameters("unused", _root,
            Path.Combine(_root, "out.log"), Path.Combine(_root, "err.log"), Path.Combine(_root, "exit.txt"), null,
            DisableSharedCompilation: true, SandboxLowIntegrity: lowIntegrity, SandboxWorktreeWritable: false);
        DispatchProcessHost.ApplyWorkerSandbox(worker, parameters,
            new WorkerSandboxPreparer(new LowIntegrityLabeler()),
            protectWorkspaceBoundary: _ => { }, protectGitMetadata: _ => { });

        Assert.Equal(1, runner.Calls);
        var observed = runner.ObservedEnvironment!.Variables;
        foreach (var home in homes)
        {
            Assert.Equal(home.Directory, observed[home.Variable]);
            Assert.Equal(worker.Environment[home.Variable], observed[home.Variable]);
        }
        Assert.Equal(worker.Environment["PATH"], observed["PATH"]);
        Assert.Equal("inherited-rustup-home", inherited.Variables["RUSTUP_HOME"]);
        Assert.False(inherited.Variables.ContainsKey("CARGO_HOME"));
    }

    private (OrchestratorWorkspace Workspace, DeclaredToolHome[] Homes) CreateWorkspace(bool projectScoped)
    {
        var projectHome = Path.Combine(_root, "project-home");
        var homes = new[] { new DeclaredToolHome("RUSTUP_HOME", Path.Combine(_root, "rustup")),
            new DeclaredToolHome("CARGO_HOME", Path.Combine(_root, "cargo")) };
        foreach (var home in homes) Directory.CreateDirectory(home.Directory);
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(projectHome);
        void WriteManifest(string directory, DeclaredToolHome[] entries) =>
            File.WriteAllText(Path.Combine(directory, "acceptance-manifest.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                toolHomes = entries.Select(home => new { variable = home.Variable, directory = home.Directory })
            }));
        // A project manifest must win over the repository manifest.
        WriteManifest(config, projectScoped ? [new("RUSTUP_HOME", _root)] : homes);
        if (projectScoped) WriteManifest(projectHome, homes);
        var workspace = new OrchestratorWorkspace(_root, _root, projectHome,
            "default", projectScoped, "default", false, "unused", "unused", "unused", "unused", "unused", "unused", "unused");
        return (workspace, homes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LowIntegrityLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
