using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Unique copied executable names scope all process observations to this fixture.
public sealed class CrosswalkDirectBufferedRunTests
{
    [Theory]
    [InlineData(0, "detected", null)]
    [InlineData(2, "exit-2", CrosswalkDiagnosticCategory.UnsupportedAdapter)]
    [InlineData(7, "exit-nonzero", CrosswalkDiagnosticCategory.NonzeroExit)]
    public async Task DirectChild_ExitCode_PreservesRawCodeAndClientCategory(
        int code, string status, CrosswalkDiagnosticCategory? category)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows fixture executables.");
        using var fixture = new Fixture();
        var request = new WorkerProcessDirectRunRequest(fixture.Binary, ["/d", "/c", $"echo [] & exit {code}"],
            fixture.Root, TimeSpan.FromMinutes(1));
        var info = WorkerProcessRunner.BuildDirectStartInfo(request.FileName, request.Arguments, request.WorkingDirectory);
        Assert.Equal(fixture.Binary, info.FileName);
        Assert.Equal(request.Arguments, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        var raw = await WorkerProcessRunner.RunBufferedDirectAsync(request, CancellationToken.None);
        Assert.Equal(code, raw.ExitCode);
        Assert.Equal("[]", raw.StandardOutput.Trim());
        var client = new CrosswalkClient(fixture.Resolver, (value, token) =>
        {
            Assert.Equal(fixture.Binary, value.FileName);
            Assert.Equal(new[] { "list", fixture.Root }, value.Arguments);
            return WorkerProcessRunner.RunBufferedDirectAsync(request, token);
        });
        var outcome = await client.ListAsync(fixture.Root, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(code, outcome.ExitCode);
        Assert.Equal(status, outcome.Status);
        if (category is null) Assert.Empty(outcome.Diagnostics);
        else Assert.Equal(category.Value, Assert.Single(outcome.Diagnostics).Category);
    }

    [Fact]
    public async Task DirectChild_Timeout_KillsObservedParentAndGrandchild()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows fixture executables.");
        using var fixture = new Fixture();
        var child = Path.Combine(fixture.Root, "child" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), child);
        File.WriteAllText(Path.Combine(fixture.Root, "wait.cmd"), $"@{Path.GetFileName(child)} -t 127.0.0.1\r\n");
        var client = new CrosswalkClient(fixture.Resolver, (_, token) => WorkerProcessRunner.RunBufferedDirectAsync(
            new(fixture.Binary, ["/d", "/c", "wait.cmd"], fixture.Root, TimeSpan.FromSeconds(30)), token));
        var run = client.ListAsync(fixture.Root, TimeSpan.FromMinutes(1), CancellationToken.None);
        // Observe both processes before the timeout. This is readiness evidence, never a
        // sleep used to guess whether the grandchild started. The bound detects a hang.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var parent = await ObserveAsync(Path.GetFileNameWithoutExtension(fixture.Binary), guard.Token);
        using var grandchild = await ObserveAsync(Path.GetFileNameWithoutExtension(child), guard.Token);
        var outcome = await run.WaitAsync(guard.Token);
        Assert.Equal("timeout", outcome.Status);
        Assert.Equal(CrosswalkDiagnosticCategory.Timeout, Assert.Single(outcome.Diagnostics).Category);
        await Task.WhenAll(parent.WaitForExitAsync(guard.Token), grandchild.WaitForExitAsync(guard.Token));
        Assert.True(parent.HasExited);
        Assert.True(grandchild.HasExited);
    }

    [Fact]
    public async Task CaptureLimitedResult_Client_PreservesCategory()
    {
        // Buffered execution currently has no capture cap; the gate seam owns this flag.
        var directory = Directory.CreateTempSubdirectory("crosswalk-capture-");
        try
        {
            var binary = Path.Combine(directory.FullName, "crosswalk.exe");
            File.WriteAllText(binary, "fixture");
            var resolver = new CrosswalkExecutableResolver(binary, runVersionAsync: (_, _) =>
                Task.FromResult(new WorkerProcessRunResult(0, "v", "")));
            var client = new CrosswalkClient(resolver, (_, _) =>
                Task.FromResult(new WorkerProcessRunResult(0, "[]", "", CaptureLimited: true)));
            var outcome = await client.ListAsync(directory.FullName, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal("capture-limited", outcome.Status);
            Assert.Equal(CrosswalkDiagnosticCategory.CaptureLimited, Assert.Single(outcome.Diagnostics).Category);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<Process> ObserveAsync(string name, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var processes = Process.GetProcessesByName(name);
            if (processes.Length == 1) return processes[0];
            foreach (var process in processes) process.Dispose();
            await Task.Yield();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("crosswalk-child-").FullName;
        internal string Binary { get; }
        internal CrosswalkExecutableResolver Resolver { get; }
        internal Fixture()
        {
            Binary = Path.Combine(Root, "parent" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Binary);
            Resolver = new(Binary, runVersionAsync: (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "v", "")));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
