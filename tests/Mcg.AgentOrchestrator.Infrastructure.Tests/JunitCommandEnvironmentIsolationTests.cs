using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique variable names, pipes, real child processes and filesystem roots. No ambient writes.
public sealed class JunitCommandEnvironmentIsolationTests
{
    [Fact]
    public async Task OverlappingChecks_OnlyDeclaredCommandReceivesLiteralInput()
    {
        var variable = "FIXTURE_" + Guid.NewGuid().ToString("N");
        var before = Environment.GetEnvironmentVariable(variable);
        Assert.Null(before);
        using var firstGate = new ChildGate();
        using var secondGate = new ChildGate();
        using var first = new JunitCommandEnvironmentFixture(variable, true, firstGate.Name);
        using var second = new JunitCommandEnvironmentFixture(variable, false, secondGate.Name);
        using var cleanup = new CancellationTokenSource();
        var firstRun = first.RunAsync(cleanup.Token);
        Task<AcceptanceCheckResult>? secondRun = null;
        try
        {
            await firstGate.Ready();
            Assert.Equal(JunitCommandEnvironmentFixture.Literal, JunitCommandEnvironmentFixture.ObservedValue(first.CommandObservation));
            Assert.False(firstRun.IsCompleted);
            secondRun = second.RunAsync(cleanup.Token);
            await secondGate.Ready();
            Assert.False(firstRun.IsCompleted); // First child still awaits release while the second is inside its command.
            Assert.False(secondRun.IsCompleted);
            Assert.Null(JunitCommandEnvironmentFixture.ObservedValue(second.CommandObservation));
            Assert.Equal(before, Environment.GetEnvironmentVariable(variable));

            var observation = Path.Combine(first.ProjectRoot, "probe-observed.json");
            var probeScript = first.WriteScript("probe", new { variable, mode = "probe", observation });
            var environment = ConductorChildEnvironment.Capture().Variables.ToDictionary(pair => pair.Key, pair => pair.Value);
            AcceptanceHermeticEnvironment.ConfigureHermeticVerificationEnvironment(environment, first.Root);
            var probe = await new ProcessEnvironmentProbeRunner().RunAsync(
                new("fixture-shell", first.Shell, ["-NoProfile", "-File", probeScript], "probe-ok"),
                new ConductorChildEnvironment(environment), first.ProjectRoot);
            Assert.True(probe.Passed, probe.OutputTail);
            Assert.Null(JunitCommandEnvironmentFixture.ObservedValue(observation));

            await firstGate.Release();
            await secondGate.Release();
            var results = await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(120));
            Assert.All(results, result => Assert.True(result.Passed, result.OutputTail));
            Assert.Equal("suite/src/fixture.case::opt-in-selected", Assert.Single(JunitCommandFixture.ReadRows(results[0])).Id);
            Assert.Equal("suite/src/fixture.case::no-opt-in", Assert.Single(JunitCommandFixture.ReadRows(results[1])).Id);
            Assert.Null(JunitCommandEnvironmentFixture.ObservedValue(first.ParserObservation));
            Assert.Null(JunitCommandEnvironmentFixture.ObservedValue(second.ParserObservation));
            Assert.Equal(before, Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            cleanup.Cancel();
            await Drain(firstRun);
            if (secondRun is not null) await Drain(secondRun);
            await firstGate.WaitForExit();
            await secondGate.WaitForExit();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellation_LeavesParentEnvironmentUnchanged(bool cancel)
    {
        var variable = "FIXTURE_" + Guid.NewGuid().ToString("N");
        var before = Environment.GetEnvironmentVariable(variable);
        using var gate = new ChildGate();
        using var fixture = new JunitCommandEnvironmentFixture(variable, true, gate.Name, exitCode: 9);
        using var lifetime = new CancellationTokenSource();
        var run = fixture.RunAsync(lifetime.Token);
        try
        {
            await gate.Ready();
            Assert.Equal(JunitCommandEnvironmentFixture.Literal, JunitCommandEnvironmentFixture.ObservedValue(fixture.CommandObservation));
            Assert.Equal(before, Environment.GetEnvironmentVariable(variable));
            if (cancel)
            {
                lifetime.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(120)));
            }
            else
            {
                await gate.Release();
                var result = await run.WaitAsync(TimeSpan.FromSeconds(120));
                Assert.False(result.Passed);
                Assert.Contains(result.CrosswalkDiagnostics!, diagnostic => diagnostic.Category == CrosswalkDiagnosticCategory.NonzeroExit);
                Assert.Equal(9, result.ExitCode);
            }
            Assert.Equal(before, Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            lifetime.Cancel();
            await Drain(run);
            await gate.WaitForExit();
        }
    }

    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(120)); }
        catch (OperationCanceledException) { }
    }

    private sealed class ChildGate : IDisposable
    {
        internal string Name { get; } = "junit-input-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _connected;
        private StreamWriter? _writer;
        private Process? _child;
        private Process? _launcher;
        internal ChildGate()
        {
            _pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _connected = _pipe.WaitForConnectionAsync();
        }
        internal async Task Ready()
        {
            try
            {
                await _connected.WaitAsync(TimeSpan.FromSeconds(120));
                using var reader = new StreamReader(_pipe, leaveOpen: true);
                var ready = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(120));
                Assert.NotNull(ready);
                using var observation = JsonDocument.Parse(ready);
                // The fixture is blocked on this pipe until release/cancellation. Pin its OS
                // handle now, so teardown observes this child rather than a later reuse of its PID.
                _child = Process.GetProcessById(observation.RootElement.GetProperty("processId").GetInt32());
                _ = _child.SafeHandle;
                if (OperatingSystem.IsWindows())
                {
                    // cmd.exe owns the command working directory independently of pwsh. Pin it
                    // while pwsh is gated: cancellation requests termination but does not await
                    // the launcher's exit, and waiting for pwsh alone cannot release both handles.
                    var parent = WindowsNativeProcessInspection.ReadParentProcessId(_child.Id);
                    Assert.Equal(ProcessInspectionStatus.Available, parent.Status);
                    _launcher = Process.GetProcessById(Assert.IsType<int>(parent.ParentProcessId));
                    _ = _launcher.SafeHandle;
                    Assert.Equal("cmd", _launcher.ProcessName, ignoreCase: true);
                }
                _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            }
            catch (TimeoutException) { throw new TimeoutException("Fixture child did not signal its ready event."); }
        }
        internal Task Release() => (_writer ?? throw new InvalidOperationException("Child ready event missing.")).WriteLineAsync("release");
        internal async Task WaitForExit()
        {
            try
            {
                await WaitForExit(_child, "Fixture child");
                await WaitForExit(_launcher, "Fixture command launcher");
            }
            finally
            {
                // The fixture's using scope ends before the gate's scope. Release the pinned
                // process handles here, before that scope recursively deletes the workspace.
                // Exit notification alone does not end this gate's ownership of the handles.
                DisposeProcesses();
            }
        }
        private static async Task WaitForExit(Process? process, string eventName)
        {
            if (process is null) return; // Ready never acquired this process's handle.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch (TimeoutException) { throw new TimeoutException($"{eventName} did not exit before directory cleanup."); }
            Assert.True(process.HasExited);
        }
        private void DisposeProcesses()
        {
            _launcher?.Dispose();
            _launcher = null;
            _child?.Dispose();
            _child = null;
        }
        public void Dispose() { DisposeProcesses(); _writer?.Dispose(); _pipe.Dispose(); }
    }
}
