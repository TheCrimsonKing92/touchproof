using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: test-owned child processes, GUID pipes and unique temporary directories.
public sealed class WorkerHelperForwarderScriptTests : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Theory]
    [InlineData("Request-HostWorkerBuild.ps1")]
    [InlineData("Submit-WorkerResult.ps1")]
    public void ForwarderSource_ContainsNoCompilation(string script)
    {
        var source = File.ReadAllText(Script(script));
        Assert.DoesNotContain("Add-Type", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet build", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mcg-orchestrator.cmd", source, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, true, 1, "worker_build_channel_unavailable")]
    [InlineData(true, true, 1, "worker_build_channel_unavailable")]
    [InlineData(false, false, 3, "REJECTED: submit tool unavailable")]
    [InlineData(true, false, 3, "REJECTED: submit tool unavailable")]
    public async Task MissingApp_ReportsOneLineAndExpectedExit(
        bool legacyShell, bool build, int exit, string message)
    {
        var arguments = build ? new[] { "-Projects", "p.csproj" } : SubmitArguments("task");
        var start = ScriptStart(legacyShell, build ? "Request-HostWorkerBuild.ps1" : "Submit-WorkerResult.ps1", arguments);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = Directory.CreateDirectory(Path.Combine(_root, "empty app")).FullName;
        var result = await CliChildProcessRunner.RunAsync(start);
        Assert.Equal(exit, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var line = Assert.Single(result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        if (build) Assert.StartsWith(message, line);
        else Assert.Equal(message, line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_PreBuiltApp_PreservesArgumentsAndOutput(bool legacyShell)
    {
        CliWorkerHelperCommandTests.SeedSubmission(_root);
        const string task = "task with \"quotes\" and trailing slash\\";
        var start = ScriptStart(legacyShell, "Submit-WorkerResult.ps1", SubmitArguments(task));
        var result = await CliChildProcessRunner.RunAsync(start);
        var path = Path.Combine(_root, "accepted result.json");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"ACCEPTED {path}{Environment.NewLine}", result.StandardOutput);
        Assert.Empty(result.StandardError);
        using var stored = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(task, stored.RootElement.GetProperty("task_id").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_RejectedInput_ForwardsStderrAndExit(bool legacyShell)
    {
        File.WriteAllText(Path.Combine(_root, "input file.json"), "{}");
        using var expectedOutput = new StringWriter();
        using var expectedError = new StringWriter();
        var args = CliWorkerHelperCommandTests.SubmissionArguments(_root, Path.Combine(_root, "accepted result.json"));
        var exit = WorkerResultSubmission.RunSubmitCommand(args[1..], expectedOutput, expectedError, () => DateTimeOffset.UnixEpoch);
        Assert.Equal(1, exit);
        var result = await CliChildProcessRunner.RunAsync(ScriptStart(legacyShell, "Submit-WorkerResult.ps1", SubmitArguments("task with spaces")));
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(expectedOutput.ToString(), result.StandardOutput);
        Assert.Equal(expectedError.ToString(), result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_PreBuiltApp_ForwardsProjectArrayAndOptions(bool legacyShell)
    {
        var name = "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator-context"));
        File.WriteAllText(Path.Combine(_root, WorkerBuildRequestChannel.DescriptorRelativePath),
            JsonSerializer.Serialize(new { version = 1, pipe = name }));
        var script = Script("Request-HostWorkerBuild.ps1").Replace("'", "''", StringComparison.Ordinal);
        var command = $"& '{script}' -Projects @('src/one project.csproj','tests/two project.csproj') " +
            "-Configuration Release -RequestKey 'forwarded-key' -BusyTimeoutSeconds 7; exit $LASTEXITCODE";
        var start = CliWorkerHelperCommandTests.ChildStart(_root, Shell(legacyShell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))]);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = AppContext.BaseDirectory;
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        var accept = server.WaitForConnectionAsync(guard.Token);
        var run = CliChildProcessRunner.RunAsync(start);
        try
        {
            await accept;
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true);
            var request = JsonSerializer.Deserialize<WorkerBuildRequest>((await reader.ReadLineAsync(guard.Token))!)!;
            Assert.Equal(new[] { "src/one project.csproj", "tests/two project.csproj" }, request.Projects);
            Assert.Equal("Release", request.Configuration);
            Assert.Equal("forwarded-key", request.RequestKey);
            await writer.WriteLineAsync(JsonSerializer.Serialize(HostBuildPipeClientTests.Response(WorkerBuildStructuralStatus.Passed)));
            await writer.FlushAsync(guard.Token);
            var result = await run;
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            Assert.Contains("worker_build_invocation=client-test-invocation; outcome=0;", result.StandardOutput);
            Assert.Contains("compiler_exit=0; structural_status=Passed; verification_exit=0;", result.StandardOutput);
        }
        finally
        {
            server.Dispose();
            await run;
        }
    }

    private ProcessStartInfo ScriptStart(bool legacyShell, string script, string[] args)
    {
        var start = CliWorkerHelperCommandTests.ChildStart(_root, Shell(legacyShell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script(script), .. args]);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = AppContext.BaseDirectory;
        return start;
    }

    private string[] SubmitArguments(string task) =>
        ["-Worktree", _root, "-Task", task, "-Path", Path.Combine(_root, "accepted result.json"),
            "-InputFile", Path.Combine(_root, "input file.json")];

    private static string Script(string name) => Path.Combine(VerifiedRepositoryRoot.Find(), "scripts", name);

    private static string Shell(bool legacy)
    {
        var shell = legacy ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe") : WorkerShell.Executable;
        Assert.True(File.Exists(shell), $"Required worker shell is unavailable: {shell}");
        return shell;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
