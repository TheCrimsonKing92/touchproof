using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its labels, executable, script and temp subtree. Launches use the
// production gate invoker, including its hermetic environment and process ownership.
internal sealed class AcceptanceGateIntegrityTestSupport : IDisposable
{
    internal const string MediumSid = "S-1-16-8192";
    internal const string LowSid = "S-1-16-4096";
    internal IcaclsIntegrityLabeler Labels { get; } = new();
    internal string Root { get; } = Directory.CreateTempSubdirectory("gate-il-").FullName;
    internal string Worktree { get; }
    private readonly string tempBoundary;

    internal AcceptanceGateIntegrityTestSupport()
    {
        Worktree = Directory.CreateDirectory(Path.Combine(Root, "worktree")).FullName;
        tempBoundary = Directory.CreateDirectory(Path.Combine(Root, "conductor-temp")).FullName;
        // AssemblyTempRedirect can make the test host's TEMP Low. A test-owned Medium
        // boundary underneath it keeps the write assertion discriminating on that host.
        Assert.True(Labels.SetIntegrity(tempBoundary, "(OI)(CI)M", recursive: true));
        Assert.True(Labels.Query(tempBoundary).Medium);
    }

    internal static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows mandatory integrity labels.");
    }

    internal async Task AssertMediumHostAsync()
    {
        var result = await RunAsync([Path.Combine(Environment.SystemDirectory, "whoami.exe"),
            "/groups", "/fo", "csv", "/nh"], Root);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(MediumSid, result.Output);
        Assert.DoesNotContain(LowSid, result.Output);
    }

    internal string CopyExecutable(string workspace)
    {
        var executable = Path.Combine(workspace, "gate-probe.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);
        return executable;
    }

    internal async Task<(GoalAcceptanceVerifier.CommandResult Result, string Created)> ProbeAsync(
        string workspace, string executable)
    {
        var name = Guid.NewGuid().ToString("N");
        var created = Path.Combine(tempBoundary, name);
        var script = Path.Combine(workspace, $"probe-{name}.cmd");
        var relative = Path.GetRelativePath(Path.GetTempPath(), created);
        Assert.False(Path.IsPathRooted(relative));
        Assert.False(relative.StartsWith("..", StringComparison.Ordinal));
        File.WriteAllText(script, $"""
            @echo off
            "{Path.Combine(Environment.SystemDirectory, "whoami.exe")}" /groups /fo csv /nh
            if errorlevel 1 exit /b 72
            mkdir "%TEMP%\{relative}"
            if errorlevel 1 exit /b 73
            echo gate-write-ok> "%TEMP%\{relative}\receipt.txt"
            if errorlevel 1 exit /b 74
            exit /b 0
            """);
        return (await RunAsync([executable, "/d", "/c", script], workspace), created);
    }

    internal async Task AssertMediumProbeAsync(string workspace)
    {
        var executable = CopyExecutable(workspace);
        Assert.True(Labels.Query(executable).Medium);
        var (result, created) = await ProbeAsync(workspace, executable);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(MediumSid, result.Output);
        Assert.DoesNotContain(LowSid, result.Output);
        Assert.True(Directory.Exists(created), result.Output);
        Assert.Equal("gate-write-ok", File.ReadAllText(Path.Combine(created, "receipt.txt")).Trim());
    }

    private static Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
        string[] arguments, string workspace) => AcceptanceProcessTestContext.RunManifestCommandAsync(
            arguments, workspace, TimeSpan.FromMinutes(2),
            new Dictionary<string, string?> { ["TEMP"] = Path.GetTempPath() },
            resolveBareCommand: false, CancellationToken.None);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
