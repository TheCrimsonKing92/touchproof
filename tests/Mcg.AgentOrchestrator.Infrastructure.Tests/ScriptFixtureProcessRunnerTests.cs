using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Xunit.Sdk;

public sealed class ScriptFixtureProcessRunnerTests
{
    [Fact]
    public void StallKillsTreeAndWaitsBeforeAssertion()
    {
        using var process = new Process();
        var events = new List<string>();
        var now = TimeSpan.Zero;
        var watch = new ChildProgressWatch(() => new(true, TimeSpan.FromSeconds(2), 12, 34),
            TimeSpan.FromMilliseconds(30_000), () => now);
        var exception = Assert.Throws<TrueException>(() => ScriptFixtureProcessRunner.AssertExited(
            process,
            "fixture expired",
            (candidate, timeout) =>
            {
                Assert.Same(process, candidate);
                events.Add($"wait:{timeout}");
                now += TimeSpan.FromMilliseconds(timeout);
                return timeout == 5_000;
            },
            candidate =>
            {
                Assert.Same(process, candidate);
                events.Add("kill-tree");
            }, watch));

        Assert.Equal(Enumerable.Repeat("wait:1000", 30).Concat(["wait:0", "kill-tree", "wait:5000"]), events);
        Assert.Contains("fixture expired", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stdout=12 bytes; stderr=34 bytes", exception.Message);
        Assert.Contains("processor time=2.000 s", exception.Message);
        Assert.Contains("time since last progress=30.000 s", exception.Message);
    }

    [Fact]
    public void ExitDoesNotKillTree()
    {
        using var process = new Process();
        var waits = 0;
        ScriptFixtureProcessRunner.AssertExited(
            process,
            "fixture expired",
            (_, timeout) =>
            {
                Assert.Equal((int)ChildProgressWatch.SamplingSlice.TotalMilliseconds, timeout);
                waits++;
                return true;
            },
            _ => Assert.Fail("Exited child must not be killed."),
            new ChildProgressWatch(() => new(true, TimeSpan.Zero, 0, 0),
                TimeSpan.FromMilliseconds(30_000), () => TimeSpan.Zero));
        Assert.Equal(1, waits);
    }

    [Fact]
    public void ChangingOutputKeepsWaitingBeyondWindow()
    {
        using var process = new Process();
        var now = TimeSpan.Zero;
        var watch = new ChildProgressWatch(() => new(true, TimeSpan.Zero, (long)now.TotalSeconds, 0),
            TimeSpan.FromMilliseconds(30_000), () => now);
        ScriptFixtureProcessRunner.AssertExited(process, "fixture stalled",
            (_, timeout) => { now += TimeSpan.FromMilliseconds(timeout); return now >= TimeSpan.FromMinutes(10); },
            _ => Assert.Fail("Progressing child must not be killed."), watch);
        Assert.Equal(TimeSpan.FromMinutes(10), now);
    }

    [Fact]
    public void OutputBeyondFormerElapsedBoundIsReturnedInFull()
    {
        const int lines = 36;
        var start = new ProcessStartInfo(WorkerShell.Executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"for ($i = 0; $i -lt {lines}; $i++) {{ " +
            "[Console]::Out.WriteLine('out-' + $i); [Console]::Out.Flush(); " +
            "[Console]::Error.WriteLine('err-' + $i); [Console]::Error.Flush(); Start-Sleep -Seconds 1 }");
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        try
        {
            var output = ScriptFixtureProcessRunner.AssertExited(process, "fixture made no progress");
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Concat(Enumerable.Range(0, lines).Select(i => $"out-{i}{Environment.NewLine}")), output.Stdout);
            Assert.Equal(string.Concat(Enumerable.Range(0, lines).Select(i => $"err-{i}{Environment.NewLine}")), output.Stderr);
            Assert.True(clock.Elapsed > TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void PathPwshFixturesUseOwnedFailsafe()
    {
        var projectDirectory = InfrastructureTestSupport.FindRepositoryRoot();
        var testDirectory = Path.Combine(projectDirectory, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        foreach (var fileName in new[]
                 {
                     "AcceptanceFailureCensusScriptTests.cs",
                     "AcceptanceGateFlakeInventoryGeneratorTests.cs",
                     "LaneTimingMeasurementScriptTests.cs"
                 })
        {
            var source = File.ReadAllText(Path.Combine(testDirectory, fileName));
            Assert.Contains("ScriptFixtureProcessRunner.AssertExited(process,", source, StringComparison.Ordinal);
            Assert.DoesNotContain("process.WaitForExit(30_000)", source, StringComparison.Ordinal);
        }
    }
}
