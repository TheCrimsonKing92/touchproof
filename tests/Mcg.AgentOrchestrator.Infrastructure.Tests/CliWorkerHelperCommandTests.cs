using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: children, temporary roots, environments and pipes belong to each test.
public sealed class CliWorkerHelperCommandTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Theory]
    [InlineData("worker-build-request", "--configuration")]
    [InlineData("submit-worker-result", "--worktree")]
    public void Catalog_RegistersStatelessVerbsAndValidatesFlags(string verb, string option)
    {
        Assert.Contains(verb, CliArgumentParser.RecognizedCommands);
        Assert.True(CliPersistentStateRunner.SkipsKernelState([verb]));
        CliCommandHelp.ThrowIfInvalidFlags([verb, option, "value"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags([verb, "--unknown-worker-option"]));
    }

    [Fact]
    public async Task Submit_ValidResult_AcceptsWithoutCreatingState()
    {
        SeedSubmission(_root);
        var before = Files(_root);
        var path = Path.Combine(_root, "accepted result.json");
        var result = await CliChildProcessRunner.RunAsync(AppStart(_root, SubmissionArguments(_root, path)));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"ACCEPTED {path}{Environment.NewLine}", result.StandardOutput);
        Assert.Empty(result.StandardError);
        using var stored = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("task with spaces", stored.RootElement.GetProperty("task_id").GetString());
        Assert.Equal("Tester", stored.RootElement.GetProperty("role").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, ".orchestrator")));
        Assert.Equal(before.Append("accepted result.json").Order(StringComparer.Ordinal), Files(_root));
    }

    [Theory]
    [InlineData("invalid", 1, "REJECTED:")]
    [InlineData("missing-option", 2, "REJECTED: usage:")]
    [InlineData("unreadable", 2, "REJECTED: input file not readable:")]
    public async Task Submit_InvalidInput_PreservesRejectionAndUsageContract(
        string scenario, int exit, string message)
    {
        SeedSubmission(_root);
        var input = Path.Combine(_root, "input file.json");
        if (scenario == "invalid") File.WriteAllText(input, "{}");
        if (scenario == "unreadable") File.Delete(input);
        var path = Path.Combine(_root, "result.json");
        var args = SubmissionArguments(_root, path);
        if (scenario == "missing-option") args = args[..^2];
        var before = Files(_root);
        var result = await CliChildProcessRunner.RunAsync(AppStart(_root, args));
        Assert.Equal(exit, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.StartsWith(message, result.StandardError);
        Assert.False(File.Exists(path));
        Assert.Equal(before, Files(_root));
        Assert.False(Directory.Exists(Path.Combine(_root, ".orchestrator")));
    }

    [Fact]
    public async Task Build_FakeHost_ExchangesRequestWithoutCreatingState()
    {
        var name = "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator-context"));
        File.WriteAllText(Path.Combine(_root, WorkerBuildRequestChannel.DescriptorRelativePath),
            JsonSerializer.Serialize(new { version = 1, pipe = name }));
        var before = Files(_root);
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        var accept = server.WaitForConnectionAsync(guard.Token);
        var run = CliChildProcessRunner.RunAsync(AppStart(_root,
            ["worker-build-request", "src/Feature.csproj", "tests/Feature.Tests.csproj", "--configuration", "Release", "--request-key", "explicit-key"]));
        try
        {
            await accept;
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true);
            var request = JsonSerializer.Deserialize<WorkerBuildRequest>((await reader.ReadLineAsync(guard.Token))!)!;
            Assert.Equal(1, request.Version);
            Assert.Equal("explicit-key", request.RequestKey);
            Assert.Equal("Release", request.Configuration);
            Assert.Equal(new[] { "src/Feature.csproj", "tests/Feature.Tests.csproj" }, request.Projects);
            await writer.WriteLineAsync(JsonSerializer.Serialize(HostBuildPipeClientTests.Response(WorkerBuildStructuralStatus.Passed)));
            await writer.FlushAsync(guard.Token);
            var result = await run;
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            Assert.Contains("worker_build_invocation=client-test-invocation; outcome=0;", result.StandardOutput);
            Assert.Contains("compiler_exit=0; structural_status=Passed; verification_exit=0;", result.StandardOutput);
            Assert.False(Directory.Exists(Path.Combine(_root, ".orchestrator")));
            Assert.Equal(before, Files(_root));
        }
        finally
        {
            server.Dispose();
            await run;
        }
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("zero-budget")]
    [InlineData("excessive-budget")]
    [InlineData("missing-value")]
    public async Task Build_InvalidArguments_ReturnsUsage(string scenario)
    {
        string[] args = scenario switch
        {
            "missing-project" => ["worker-build-request"],
            "zero-budget" => ["worker-build-request", "p.csproj", "--busy-timeout-seconds", "0"],
            "excessive-budget" => ["worker-build-request", "p.csproj", "--busy-timeout-seconds", "2147484"],
            _ => ["worker-build-request", "p.csproj", "--configuration"]
        };
        var result = await CliChildProcessRunner.RunAsync(AppStart(_root, args));
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("Usage: worker-build-request ", result.StandardError);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(Files(_root));
    }

    internal static ProcessStartInfo AppStart(string root, string[] args)
    {
        var app = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        Assert.True(File.Exists(app), "The test output must contain the pre-built App assembly.");
        Assert.True(File.Exists(Path.ChangeExtension(app, ".runtimeconfig.json")), "The test output must contain the App runtime config.");
        return ChildStart(root, "dotnet", [app, .. args]);
    }

    internal static ProcessStartInfo ChildStart(string root, string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(start.Environment, VerifiedRepositoryRoot.Find());
        start.Environment["TEMP"] = root;
        start.Environment["TMP"] = root;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    internal static void SeedSubmission(string root)
    {
        Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
        var project = Path.Combine(root, WorkerResultSubmissionTests.Project);
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "");
        File.WriteAllText(Path.Combine(root, "input file.json"), WorkerResultSubmissionTests.Fixture, Encoding.Unicode);
    }

    internal static string[] SubmissionArguments(string root, string path) =>
        ["submit-worker-result", "--worktree", root, "--task", "task with spaces", "--path", path,
            "--input", Path.Combine(root, "input file.json")];

    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal).ToArray();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
