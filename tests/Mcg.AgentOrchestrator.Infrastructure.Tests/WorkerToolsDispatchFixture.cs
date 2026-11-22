using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class WorkerToolsDispatchFixture : IDisposable
{
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private readonly InProcessGitFixture _source = new(commit: false);
    private readonly InProcessGitFixture _worker = new();
    private readonly string _pin;

    internal WorkerToolsDispatchFixture()
    {
        File.WriteAllText(Path.Combine(_source.Repository, "NuGet.Config"), """
            <configuration><packageSources><clear /></packageSources></configuration>
            """);
        File.WriteAllText(Path.Combine(_source.Repository, "Wt.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>wt</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <None Update="version.txt" CopyToPublishDirectory="Always" />
              </ItemGroup>
              <Target Name="RecordFixturePublish" BeforeTargets="Publish">
                <WriteLinesToFile File="{SecurityElement.Escape(BuildLog)}" Lines="published" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_source.Repository, "Program.cs"), """System.Console.WriteLine("wt fixture");""");
        File.WriteAllText(Path.Combine(_source.Repository, "version.txt"), "pinned");
        _source.Git("add", ".");
        _source.Git("commit", "-m", "Pinned tool");
        _pin = _source.Git("rev-parse", "--short=7", "HEAD");
        File.WriteAllText(Path.Combine(_source.Repository, "version.txt"), "head");
        _source.Git("commit", "-am", "Newer tool");
        File.WriteAllText(Path.Combine(_source.Repository, "version.txt"), "uncommitted");
    }

    internal string BuildLog => Path.Combine(_root, "build.log");

    internal (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, string Prompt) Prepare(
        AgentRole role, bool fails = false, bool useClaude = false)
    {
        var config = Path.Combine(_root, fails ? "broken.json" : "worker-tools.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            pinnedCommit = _pin,
            sourceRepository = _source.Repository,
            project = fails ? "Missing.csproj" : "Wt.csproj"
        }));
        var installer = new WorkerToolsInstaller(config, Path.Combine(_root, fails ? "failed-cache" : "tool-cache"));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Prepare a worker tools fixture.", role);
        var goal = kernel.CreateGoal("Exercise worker tools dispatch.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var profile = useClaude
            ? WorkerProfileCatalog.Default().GetRequired("claude-cli")
            : new WorkerProfile("local", "fixture-worker");
        var prepared = WorkerProfileDispatcher.PrepareTask(kernel, goal, task, profile,
            Path.Combine(_root, "prompts"), _worker.Worktree, DateTimeOffset.UtcNow,
            variables: useClaude ? new Dictionary<string, string?> { ["subscriptionModelName"] = "claude-opus-5" } : null,
            integrationBranch: _worker.Branch,
            workerTools: installer);
        return (kernel, goal, prepared.Task, File.ReadAllText(prepared.PromptPath));
    }

    internal string LogRoot => Path.Combine(_root, "logs");

    internal ProcessStartInfo CaptureStart(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        ProcessStartInfo? captured = null;
        var runner = new BackgroundDispatchRunner(disableProcessStart: false, startProcess: info =>
        {
            captured = info;
            throw new LaunchCapturedException();
        });
        Assert.Throws<LaunchCapturedException>(() => runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, LogRoot,
            sandboxOptions: new WorkerSandboxOptions(false, "fixture", "fixture")));
        return Assert.IsType<ProcessStartInfo>(captured);
    }

    public void Dispose()
    {
        _source.Dispose();
        _worker.Dispose();
        SharedTestSupport.RemoveTempDirectory(_root);
    }

    private sealed class LaunchCapturedException : Exception;
}
