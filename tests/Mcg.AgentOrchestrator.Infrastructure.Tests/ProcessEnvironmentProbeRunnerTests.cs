using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique marker directories, explicit child environments and identity-bound cleanup.
public sealed class ProcessEnvironmentProbeRunnerTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Exercises Windows anonymous pipes.", SkipUnless = nameof(IsWindows))]
    public async Task Deadline_HangingTree_KillsBeforeWaitingForPipeEof()
    {
        await using var fixture = new ProbeFixture();
        var clock = new ManualStewardTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);
        var run = fixture.Start(new ProcessEnvironmentProbeRunner(timeout, clock), fixture.TreeScript());
        var root = await fixture.Observe("root");
        var child = await fixture.Observe("child");
        Assert.False(root.HasExited);
        Assert.False(child.HasExited);

        // Apply the deadline only after both processes exist and hold the redirected pipes.
        clock.Advance(timeout);
        var result = await run.WaitAsync(TestHangGuard.HangSafetyBound);

        Assert.False(result.Passed);
        Assert.Equal("timeout", result.Outcome);
        Assert.Null(result.ExitCode);
        await Task.WhenAll(root.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(TestHangGuard.Bound);
        Assert.True(root.HasExited);
        Assert.True(child.HasExited);
    }

    [Fact(Skip = "Exercises Windows anonymous pipes.", SkipUnless = nameof(IsWindows))]
    public async Task RootExit_DescendantRetainsPipe_ReturnsDrainTimeout()
    {
        await using var fixture = new ProbeFixture();
        var clock = new ManualStewardTimeProvider();
        var run = fixture.Start(new ProcessEnvironmentProbeRunner(TimeSpan.FromSeconds(5), clock),
            fixture.TreeScript(exitOnSignal: true));
        var root = await fixture.Observe("root");
        var child = await fixture.Observe("child");

        File.WriteAllText(fixture.PathFor("release"), "exit");
        await root.WaitForExitAsync().WaitAsync(TestHangGuard.Bound);
        var result = await run.WaitAsync(TestHangGuard.HangSafetyBound);

        Assert.False(result.Passed);
        Assert.Equal("output-drain-timeout", result.Outcome);
        Assert.Contains("descendant-ready", result.OutputTail);
        Assert.False(child.HasExited);
    }

    [Fact(Skip = "Uses the Windows PowerShell executable.", SkipUnless = nameof(IsWindows))]
    public async Task Stdin_ClosedAtStart_AllowsProbeToFinish()
    {
        await using var fixture = new ProbeFixture();
        var run = fixture.Start(new ProcessEnvironmentProbeRunner(),
            "$inputText = [Console]::In.ReadToEnd(); if ($inputText.Length -ne 0) { exit 9 }; [Console]::Write('input-closed')");

        var result = await run.WaitAsync(TestHangGuard.HangSafetyBound);

        Assert.True(result.Passed, result.Outcome + ": " + result.OutputTail);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("input-closed", result.OutputTail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void Timeout_OutsideExecutionBound_IsRejected(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessEnvironmentProbeRunner(TimeSpan.FromSeconds(seconds)));

    private sealed class ProbeFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("environment-probe-");
        private readonly CancellationTokenSource _abort = new();
        private readonly List<(Process Process, ConductorSupervisorProcessIdentity Identity)> _owned = [];
        private Task<EnvironmentProbeResult>? _run;
        private static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        internal string PathFor(string name) => Path.Combine(_directory.FullName, name);
        private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        internal Task<EnvironmentProbeResult> Start(ProcessEnvironmentProbeRunner runner, string script)
        {
            var environment = new ConductorChildEnvironment(new Dictionary<string, string?>
            {
                ["SystemRoot"] = Directory.GetParent(Environment.SystemDirectory)!.FullName,
                ["PATH"] = Environment.SystemDirectory,
                ["TEMP"] = _directory.FullName,
                ["TMP"] = _directory.FullName
            });
            var tool = new EnvironmentToolRequirement("test-probe", PowerShell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script)]);
            return _run = runner.RunAsync(tool, environment, _directory.FullName, _abort.Token);
        }

        internal string TreeScript(bool exitOnSignal = false)
        {
            var childScript = "[Console]::Write('descendant-ready'); " + Ready("child") +
                "[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)";
            var rootWait = exitOnSignal
                ? $"while (![IO.File]::Exists({Quote(PathFor("release"))})) {{ [Threading.Thread]::Sleep(10) }}"
                : "[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)";
            return "$start = [Diagnostics.ProcessStartInfo]::new(); " +
                $"$start.FileName = {Quote(PowerShell)}; " +
                "$start.UseShellExecute = $false; $start.CreateNoWindow = $true; " +
                // Redirecting input selects explicit standard handles; output inherits our probe pipes.
                "$start.RedirectStandardInput = $true; " +
                $"$start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand {Encode(childScript)}'; " +
                "$child = [Diagnostics.Process]::Start($start); $child.StandardInput.Close(); " +
                Ready("root") + rootWait;
        }

        private string Ready(string name) =>
            $"[IO.File]::WriteAllText({Quote(PathFor(name + ".tmp"))}, $PID.ToString()); " +
            $"[IO.File]::Move({Quote(PathFor(name + ".tmp"))}, {Quote(PathFor(name))}); ";

        internal async Task<Process> Observe(string name)
        {
            await MarkerFileProbe.WaitAsync(PathFor(name), TestHangGuard.Bound);
            var process = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(PathFor(name))));
            var identity = TestOwnedProcessStop.Identify(process);
            Assert.NotNull(identity);
            _owned.Add((process, identity));
            return process;
        }

        public async ValueTask DisposeAsync()
        {
            _abort.Cancel();
            try
            {
                if (_run is not null) await _run.WaitAsync(TestHangGuard.HangSafetyBound);
            }
            finally
            {
                foreach (var (process, identity) in _owned.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (!process.HasExited && TestOwnedProcessStop.StopTreeIfSame(identity))
                            await process.WaitForExitAsync().WaitAsync(TestHangGuard.Bound);
                    }
                    finally { process.Dispose(); }
                }
                _abort.Dispose();
                _directory.Delete(recursive: true);
            }
        }
    }
}
