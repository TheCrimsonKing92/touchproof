using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repository, pipe, capture files and native child job.
public sealed class WorkerBuildRequestChannelTerminalResponseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceThrows_AdmittedClientReceivesOneTerminalFailure(bool asynchronous)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var message = "fixture scope failure " + Guid.NewGuid().ToString("N");
        var service = new ThrowingService(message, asynchronous);
        fixture.Channel = new WorkerBuildRequestChannel(fixture.RepositoryRoot, service);
        var start = new ProcessStartInfo(WorkerShell.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.RepositoryRoot
        };
        // Explicit environment keeps provider and handoff settings out of the real client path.
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "PATH", "PATHEXT", "COMSPEC",
            "ProgramFiles", "ProgramFiles(x86)", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["MCG_ORCHESTRATOR_APP_OUTPUT"] = AppContext.BaseDirectory;
        var temp = Path.Combine(fixture.Root, "terminal-client-temp");
        Directory.CreateDirectory(temp);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File",
            Path.Combine(WorkerBuildRequestFixture.FindRepository(), "scripts", "Request-HostWorkerBuild.ps1"),
            "-Projects", WorkerBuildRequestFixture.Project }) start.ArgumentList.Add(arg);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll")));
        var outputPath = Path.Combine(fixture.Root, "terminal-client.out.log");
        var errorPath = Path.Combine(fixture.Root, "terminal-client.err.log");
        fixture.Worker = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(start, outputPath, errorPath);
        fixture.Channel.Register(fixture.Worker.Process, fixture.Worker.Group);
        fixture.Worker.Resume();
        // Hang-only failsafe: the owned job's exit is the synchronization event.
        await fixture.Worker.WaitForOwnedExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(4));
        fixture.Channel.Dispose();
        fixture.Channel = null;

        var output = File.ReadAllText(outputPath);
        var error = File.ReadAllText(errorPath);
        Assert.Equal(1, fixture.Worker.TryReadOwnedExitCode());
        Assert.Equal(1, service.Calls);
        var terminal = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("worker_build_invocation=", StringComparison.Ordinal));
        Assert.StartsWith("worker_build_invocation=; outcome=7;", terminal);
        Assert.Contains(nameof(IOException), terminal, StringComparison.Ordinal);
        Assert.Contains(message, terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("closed without a terminal response", output + error, StringComparison.Ordinal);
        Assert.Contains("verification_exit=1;", output, StringComparison.Ordinal);
    }

    private sealed class ThrowingService(string message, bool asynchronous) : IWorkerBuildRequestService
    {
        internal int Calls;
        public Task<WorkerBuildResponse> ExecuteAsync(WorkerBuildRequest request, WorkerBuildProcess worker,
            CancellationToken workerLifetime)
        {
            Interlocked.Increment(ref Calls);
            if (asynchronous) return Task.FromException<WorkerBuildResponse>(new IOException(message));
            throw new IOException(message);
        }
    }
}
