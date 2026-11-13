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
    [InlineData(false, true, 1, "worker_build_client_missing")]
    [InlineData(true, true, 1, "worker_build_client_missing")]
    [InlineData(false, false, 3, "REJECTED: submit tool unavailable")]
    [InlineData(true, false, 3, "REJECTED: submit tool unavailable")]
    public async Task MissingApp_ReportsOneLineAndExpectedExit(
        bool legacyShell, bool build, int exit, string message)
    {
        var arguments = build ? new[] { "-Projects", "p.csproj" } : SubmitArguments("task");
        var start = ScriptStart(legacyShell, build ? "Request-HostWorkerBuild.ps1" : "Submit-WorkerResult.ps1", arguments);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = Directory.CreateDirectory(Path.Combine(_root, "empty app")).FullName;
        start.Environment.Remove("MCG_ORCHESTRATOR_HOME");
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
        var start = BuildRequestStart(legacyShell, Script("Request-HostWorkerBuild.ps1"));
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = AppContext.BaseDirectory;
        await AssertBuildForwarded(start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_NoOverrideNoLocalApp_ForwardsThroughHomeApp(bool legacyShell)
    {
        var script = CreateFixtureBuildScript();
        var home = Path.Combine(_root, "orchestrator home");
        var output = AppOutput(home);
        // The test host can keep its temp root below the app output; never copy that tree into itself.
        var sources = Directory.EnumerateFiles(AppContext.BaseDirectory).Concat(
            Directory.EnumerateDirectories(AppContext.BaseDirectory)
                .Where(path => !string.Equals(Path.GetFileName(path), ".test-tmp", StringComparison.OrdinalIgnoreCase))
                .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)));
        foreach (var source in sources)
        {
            var destination = Path.Combine(output, Path.GetRelativePath(AppContext.BaseDirectory, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        Assert.True(File.Exists(Path.Combine(output, "Mcg.AgentOrchestrator.App.dll")));
        Assert.True(File.Exists(Path.Combine(output, "Mcg.AgentOrchestrator.App.runtimeconfig.json")));
        var start = BuildRequestStart(legacyShell, script);
        start.Environment.Remove("MCG_ORCHESTRATOR_APP_OUTPUT");
        start.Environment["MCG_ORCHESTRATOR_HOME"] = home;
        await AssertBuildForwarded(start);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_NoAppInEitherLocation_ReportsOneLine(bool legacyShell)
    {
        var script = CreateFixtureBuildScript();
        var home = Directory.CreateDirectory(Path.Combine(_root, "empty home")).FullName;
        var app = Path.Combine(AppOutput(home), "Mcg.AgentOrchestrator.App.dll");
        Assert.False(File.Exists(app));
        var start = CliWorkerHelperCommandTests.ChildStart(_root, Shell(legacyShell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Projects", "p.csproj"]);
        start.Environment.Remove("MCG_ORCHESTRATOR_APP_OUTPUT");
        start.Environment["MCG_ORCHESTRATOR_HOME"] = home;
        var result = await CliChildProcessRunner.RunAsync(start);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var line = Assert.Single(result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal($"worker_build_client_missing app=\"{app}\"", line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_MissingClient_ReportsResolvedPathWithoutChannelFailure(bool legacyShell)
    {
        var script = CreateFixtureBuildScript();
        var localApp = Path.Combine(AppOutput(Path.GetDirectoryName(Path.GetDirectoryName(script)!)!),
            "Mcg.AgentOrchestrator.App.dll");
        var start = FixtureBuildStart(legacyShell, script);
        start.Environment.Remove("MCG_ORCHESTRATOR_APP_OUTPUT");
        start.Environment.Remove("MCG_ORCHESTRATOR_HOME");
        await AssertMissingClient(start, localApp);

        var output = Directory.CreateDirectory(Path.Combine(_root, "empty override")).FullName;
        var home = Path.Combine(_root, "fallback home");
        Directory.CreateDirectory(AppOutput(home));
        File.WriteAllText(Path.Combine(AppOutput(home), "Mcg.AgentOrchestrator.App.dll"), "unused fallback");
        start = FixtureBuildStart(legacyShell, script);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = output;
        start.Environment["MCG_ORCHESTRATOR_HOME"] = home;
        await AssertMissingClient(start, Path.Combine(output, "Mcg.AgentOrchestrator.App.dll"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_LaunchException_RetainsChannelUnavailableReason(bool legacyShell)
    {
        var script = CreateFixtureBuildScript();
        var output = Directory.CreateDirectory(Path.Combine(_root, "dummy app")).FullName;
        var app = Path.Combine(output, "Mcg.AgentOrchestrator.App.dll");
        File.WriteAllText(app, "not executed");
        var start = FixtureBuildStart(legacyShell, script);
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = output;
        start.Environment.Remove("MCG_ORCHESTRATOR_HOME");
        start.Environment["PATH"] = Directory.CreateDirectory(Path.Combine(_root, "empty path")).FullName;

        var result = await CliChildProcessRunner.RunAsync(start);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal($"worker_build_channel_unavailable app=\"{app}\"{Environment.NewLine}", result.StandardError);
        Assert.DoesNotContain("worker_build_client_missing", result.StandardError);
    }

    private ProcessStartInfo FixtureBuildStart(bool legacyShell, string script) =>
        CliWorkerHelperCommandTests.ChildStart(_root, Shell(legacyShell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Projects", "p.csproj"]);

    private static async Task AssertMissingClient(ProcessStartInfo start, string app)
    {
        Assert.False(File.Exists(app));
        var result = await CliChildProcessRunner.RunAsync(start);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal($"worker_build_client_missing app=\"{app}\"{Environment.NewLine}", result.StandardError);
        Assert.DoesNotContain("worker_build_channel_unavailable", result.StandardError);
    }

    private ProcessStartInfo BuildRequestStart(bool legacyShell, string script)
    {
        var escapedScript = script.Replace("'", "''", StringComparison.Ordinal);
        var command = $"& '{escapedScript}' -Projects @('src/one project.csproj','tests/two project.csproj') " +
            "-Configuration Release -RequestKey 'forwarded-key' -BusyTimeoutSeconds 7; exit $LASTEXITCODE";
        return CliWorkerHelperCommandTests.ChildStart(_root, Shell(legacyShell),
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))]);
    }

    private async Task AssertBuildForwarded(ProcessStartInfo start)
    {
        var name = "mcg-worker-build-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator-context"));
        File.WriteAllText(Path.Combine(_root, WorkerBuildRequestChannel.DescriptorRelativePath),
            JsonSerializer.Serialize(new { version = 1, pipe = name }));
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var guard = new CancellationTokenSource(TestHangGuard.HangSafetyBound);
        var accept = server.WaitForConnectionAsync(guard.Token);
        var run = CliChildProcessRunner.RunAsync(start);
        try
        {
            if (await Task.WhenAny(accept, run).WaitAsync(guard.Token) == run)
            {
                var earlyExit = await run;
                Assert.Fail($"Build forwarder exited before connecting to the named pipe: exit={earlyExit.ExitCode}; stderr={earlyExit.StandardError}");
            }
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
        catch (OperationCanceledException ex) when (guard.IsCancellationRequested)
        {
            throw new TimeoutException("Build forwarder did not complete the named-pipe request exchange.", ex);
        }
        finally
        {
            server.Dispose();
            await run;
        }
    }

    private string CreateFixtureBuildScript()
    {
        var worktree = Path.Combine(_root, "worktree");
        var scripts = Directory.CreateDirectory(Path.Combine(worktree, "scripts")).FullName;
        var script = Path.Combine(scripts, "Request-HostWorkerBuild.ps1");
        File.Copy(Script("Request-HostWorkerBuild.ps1"), script);
        Assert.False(File.Exists(Path.Combine(AppOutput(worktree), "Mcg.AgentOrchestrator.App.dll")));
        return script;
    }

    private static string AppOutput(string root) =>
        Path.Combine(root, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0");

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
