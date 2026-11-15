// Reads the in-place sweep wrap sites; no sweep-body extraction was needed.
using System.Reflection;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: source reads use the verified checkout; heartbeat clocks, gates and output are private.
public sealed class ConductorStartupPhaseWiringTests
{
    [Xunit.Fact]
    public void PhaseList_NineStartupPhases_WiringReferencesNamedConstants()
    {
        Assert.Equal(new[]
        {
            "state-load", "sweep-kernel-load", "terminal-sweep", "metadata-exclusion-count", "sweep-surfacing",
            "sweep-persistence", "orphan-worktree-sweep", "remote-mirror-start", "loop-setup"
        }, ConductorStartupHeartbeat.Phases.All);
        var root = VerifiedRepositoryRoot.Find();
        var sweep = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner.cs"));
        var handler = File.ReadAllText(Path.Combine(root, "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals.cs"));
        var fields = typeof(ConductorStartupHeartbeat.Phases).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        Assert.Equal(9, fields.Length);
        foreach (var field in fields)
        {
            var name = (string)field.GetRawConstantValue()!;
            Assert.Matches("^[a-z]+(-[a-z]+)*$", name);
            var reference = $"ConductorStartupHeartbeat.Phases.{field.Name}";
            if (field.Name == nameof(ConductorStartupHeartbeat.Phases.StateLoad))
            {
                var start = sweep.IndexOf("var kernel = RunConductPreLoopStartup(", StringComparison.Ordinal);
                Assert.True(start >= 0, "Initial startup call must exist.");
                var end = sweep.IndexOf("RunStartupSweep);", start, StringComparison.Ordinal);
                Assert.True(end > start, "Initial startup call must include the sweep callback.");
                var startup = sweep[start..end];
                Assert.Contains($"ConductorStartupHeartbeat.Shipped.Run({reference}, () => LoadInitialConductLoopKernelWithTransientHold(", startup, StringComparison.Ordinal);
                Assert.DoesNotContain($"\"{name}\"", startup, StringComparison.Ordinal);
                Assert.Equal(1, sweep.Split($"\"{name}\"", StringSplitOptions.None).Length - 1);
                Assert.Contains("slowStep = \"state-load\";", sweep, StringComparison.Ordinal);
                continue;
            }
            Assert.Contains(field.Name == "LoopSetup"
                ? $"ConductorStartupHeartbeat.Shipped.Begin({reference})"
                : $"heartbeat.Run({reference},", field.Name == "LoopSetup" ? handler : sweep, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{name}\"", sweep, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{name}\"", handler, StringComparison.Ordinal);
        }
        Assert.Contains("using var loopSetup =", handler, StringComparison.Ordinal);
        Assert.Contains("loadTrxCoherenceVerdicts: directory => { TrxCoherenceVerdictStore.Load(directory); TrxCoherenceVerdictStore.EnableAppend(directory); loopSetup.Dispose(); }", handler, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task StateLoadPhase_BodyOutlastsInterval_EmitsProgressBeforeReady()
    {
        var lines = new ConcurrentQueue<string>();
        var delays = Channel.CreateUnbounded<TaskCompletionSource>();
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        long elapsedTicks = 0;
        var heartbeat = new ConductorStartupHeartbeat(line =>
        {
            lines.Enqueue(line);
            if (line == "STARTUP_PROGRESS phase=state-load elapsed_ms=15000")
                progress.TrySetResult(line);
        }, () => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref elapsedTicks)),
        (interval, cancellationToken) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(15), interval);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(delays.Writer.TryWrite(release));
            return release.Task.WaitAsync(cancellationToken);
        });
        var run = Task.Run(() => CliPersistentStateRunner.RunConductPreLoopStartup(
            () => true,
            () => heartbeat.Run(ConductorStartupHeartbeat.Phases.StateLoad, () =>
            {
                loadEntered.TrySetResult();
                return releaseLoad.Task.GetAwaiter().GetResult();
            }),
            state => $"LOOP_READY state={state}", lines.Enqueue,
            state => { lines.Enqueue("sweep"); return state; }));
        try
        {
            await WaitFor(loadEntered.Task, "state load body entered");
            var delay = delays.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            await WaitFor(delay, "heartbeat delay requested");
            Interlocked.Exchange(ref elapsedTicks, TimeSpan.FromSeconds(15).Ticks);
            (await delay).SetResult();
            await WaitFor(progress.Task, "state-load interval progress emitted");
            Assert.Equal("STARTUP_PROGRESS phase=state-load elapsed_ms=15000", await progress.Task);
            Assert.DoesNotContain(lines, line => line.StartsWith("LOOP_READY", StringComparison.Ordinal));
        }
        finally
        {
            releaseLoad.TrySetResult("loaded");
            await WaitFor(run, "state load, readiness and sweep completed");
        }
        Assert.Equal("loaded", await run);
        Assert.Equal(new[]
        {
            "STARTUP_PROGRESS phase=state-load elapsed_ms=0",
            "STARTUP_PROGRESS phase=state-load elapsed_ms=15000",
            "STARTUP_PROGRESS phase=state-load elapsed_ms=15000",
            "LOOP_READY state=loaded", "sweep"
        }, lines.ToArray());
    }

    private static async Task WaitFor(Task task, string eventName)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException exception) { throw new TimeoutException($"Timed out waiting for {eventName}", exception); }
    }
}
