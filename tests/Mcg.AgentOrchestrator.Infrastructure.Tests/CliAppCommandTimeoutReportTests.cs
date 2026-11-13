using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: each test owns its child and pipes; no shared files or environment mutation.
public sealed class CliAppCommandTimeoutReportTests
{
    private const string OutputSentinel = "stdout-terminal-sentinel";
    private const string ErrorSentinel = "stderr-terminal-sentinel";
    private const string FloodLine = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789ABCD";
    private const int FloodLines = 512;

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Run_ChildOutlivesDeadline_ReportsIdentityAndCappedStreamTails()
    {
        var start = ShellStart(FloodCommand(wait: true));
        string[] arguments = ["start-dispatch", "--goal", "goal with spaces", "", "a\"b", "C:\\folder with spaces\\"];
        Process? child = null;
        ConductorSupervisorProcessIdentity? childIdentity = null;
        var pid = 0;
        var elapsed = Stopwatch.StartNew();
        try
        {
            var run = Task.Run(() => CliAppCommandTimeoutReport.Run(start, arguments, 1000, process =>
            {
                pid = process.Id;
                childIdentity = TestOwnedProcessStop.Identify(process);
                child = Process.GetProcessById(pid);
                _ = child.SafeHandle; // Hold the original process identity through test cleanup.
            }));
            // Hang-only ceiling: detect a missing timeout report well before the child's 30 s wait.
            await TestHangGuard.CompletesWithinAsync(run, TimeSpan.FromMilliseconds(10000),
                "CLI timeout report after bounded termination and concurrent output drain");
            var error = await Assert.ThrowsAsync<TimeoutException>(() => run);
            elapsed.Stop();

            Assert.True(elapsed.ElapsedMilliseconds < 10000,
                "Hang guard: timeout reporting did not finish before the 10 s cleanup ceiling.");
            Assert.Contains("App command did not exit within 1000 ms.", error.Message, StringComparison.Ordinal);
            Assert.Contains("Command arguments: start-dispatch --goal \"goal with spaces\" \"\" \"a\\\"b\" \"C:\\\\folder with spaces\\\\\"\n",
                error.Message, StringComparison.Ordinal);
            Assert.True(pid > 0);
            Assert.Contains($"PID: {pid.ToString(CultureInfo.InvariantCulture)}\n", error.Message, StringComparison.Ordinal);
            var elapsedLine = error.Message.Split('\n').Single(line => line.StartsWith("Elapsed milliseconds: ", StringComparison.Ordinal));
            Assert.True(long.TryParse(elapsedLine["Elapsed milliseconds: ".Length..],
                NumberStyles.None, CultureInfo.InvariantCulture, out var reportedMilliseconds));
            Assert.InRange(reportedMilliseconds, 0, elapsed.ElapsedMilliseconds);
            Assert.Contains("Termination: ", error.Message, StringComparison.Ordinal);
            Assert.Contains("Drain: ", error.Message, StringComparison.Ordinal);

            var (stdout, stderr) = StreamPayloads(error.Message);
            Assert.Contains(OutputSentinel, stdout, StringComparison.Ordinal);
            Assert.Contains(ErrorSentinel, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(ErrorSentinel, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(OutputSentinel, stderr, StringComparison.Ordinal);
            Assert.StartsWith(CliAppCommandTimeoutReport.TruncationMarker, stdout, StringComparison.Ordinal);
            Assert.StartsWith(CliAppCommandTimeoutReport.TruncationMarker, stderr, StringComparison.Ordinal);
            Assert.InRange(stdout.Length, 1, 16384);
            Assert.InRange(stderr.Length, 1, 16384);
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        var stop = Task.Factory.StartNew(() =>
                        {
                            if (!child.HasExited)
                                TestOwnedProcessStop.StopTreeIfSame(childIdentity);
                        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        await TestHangGuard.WaitAsync(stop, "owned timeout fixture child termination");
                        await TestHangGuard.WaitAsync(child.WaitForExitAsync(), "owned timeout fixture child exit");
                    }
                }
                finally { child.Dispose(); }
            }
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void Run_ChildExitsWithLargeOutput_PreservesExitCodeAndFullStreams()
    {
        var start = ShellStart(FloodCommand(wait: false));
        var result = CliAppCommandTimeoutReport.Run(start, start.ArgumentList.ToArray(), 60000);
        var lines = string.Concat(Enumerable.Repeat(FloodLine + Environment.NewLine, FloodLines));

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(lines + OutputSentinel + Environment.NewLine, result.Stdout);
        Assert.Equal(lines + ErrorSentinel + Environment.NewLine, result.Stderr);
        Assert.True(result.Stdout.Length > 65536);
        Assert.True(result.Stderr.Length > 65536);
    }

    [Fact]
    public void RenderTail_EmptyAndExactCap_OmitsTruncationMarker()
    {
        Assert.Equal("(empty)", CliAppCommandTimeoutReport.RenderTail(""));
        Assert.Equal("short output", CliAppCommandTimeoutReport.RenderTail("short output"));
        var exactCap = new string('x', 16384);
        Assert.Equal(exactCap, CliAppCommandTimeoutReport.RenderTail(exactCap));
    }

    [Fact]
    public void RenderTail_OneOverCap_RetainsFinalCharactersWithinCap()
    {
        var text = new string('x', 16384) + "z";
        var result = CliAppCommandTimeoutReport.RenderTail(text);

        Assert.Equal(16384, result.Length);
        Assert.Equal("[... truncated ...]" + text[(1 + CliAppCommandTimeoutReport.TruncationMarker.Length)..], result);
        Assert.EndsWith("z", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTail_CutInsideSurrogatePair_DropsOrphanedLowSurrogate()
    {
        var retained = 16384 - CliAppCommandTimeoutReport.TruncationMarker.Length;
        var text = new string('x', 30) + "\U0001F642" + new string('y', retained - 1);
        var result = CliAppCommandTimeoutReport.RenderTail(text);

        Assert.Equal("[... truncated ...]" + new string('y', retained - 1), result);
        Assert.True(result.Length < 16384);
    }

    private static (string Stdout, string Stderr) StreamPayloads(string message)
    {
        var outputHeader = message.IndexOf("Standard output [", StringComparison.Ordinal);
        Assert.True(outputHeader >= 0, "Missing standard output report.");
        var outputStart = message.IndexOf('\n', outputHeader) + 1;
        var errorHeader = message.IndexOf("\nStandard error [", outputStart, StringComparison.Ordinal);
        Assert.True(errorHeader >= outputStart, "Missing standard error report.");
        var errorStart = message.IndexOf('\n', errorHeader + 1) + 1;
        return (message[outputStart..errorHeader], message[errorStart..]);
    }

    private static string FloodCommand(bool wait)
    {
        if (OperatingSystem.IsWindows())
        {
            var finish = wait ? "ping -n 31 127.0.0.1 >nul" : "exit /b 3";
            return $"(for /L %i in (1,1,{FloodLines}) do @echo {FloodLine}) & echo {OutputSentinel}& " +
                $"(for /L %i in (1,1,{FloodLines}) do @echo {FloodLine}) 1>&2 & 1>&2 echo {ErrorSentinel}& {finish}";
        }

        var ending = wait ? "sleep 30" : "exit 3";
        return $"i=0; while [ $i -lt {FloodLines} ]; do printf '%s\\n' '{FloodLine}'; i=$((i+1)); done; " +
            $"printf '%s\\n' '{OutputSentinel}'; i=0; " +
            $"while [ $i -lt {FloodLines} ]; do printf '%s\\n' '{FloodLine}' >&2; i=$((i+1)); done; " +
            $"printf '%s\\n' '{ErrorSentinel}' >&2; {ending}";
    }

    private static ProcessStartInfo ShellStart(string command)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
        }
        else
        {
            start.ArgumentList.Add("-c");
        }
        start.ArgumentList.Add(command);
        return start;
    }
}
