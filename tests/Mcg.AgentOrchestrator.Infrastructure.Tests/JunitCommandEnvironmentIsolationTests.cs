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
        using var first = new JunitCommandEnvironmentFixture(variable, true, firstGate.Name)
            { CleanupProcessEvidence = firstGate.DescribeProcesses };
        using var second = new JunitCommandEnvironmentFixture(variable, false, secondGate.Name)
            { CleanupProcessEvidence = secondGate.DescribeProcesses };
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
        var fixture = new JunitCommandEnvironmentFixture(variable, true, gate.Name, exitCode: 9)
            { CleanupProcessEvidence = gate.DescribeProcesses };
        using var lifetime = new CancellationTokenSource();
        var run = fixture.RunAsync(lifetime.Token);
        Exception? failure = null;
        try
        {
            await gate.Ready();
            Assert.Equal(JunitCommandEnvironmentFixture.Literal, JunitCommandEnvironmentFixture.ObservedValue(fixture.CommandObservation));
            Assert.Equal(before, Environment.GetEnvironmentVariable(variable));
            if (cancel)
            {
                lifetime.Cancel();
                var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => run.WaitAsync(TimeSpan.FromSeconds(120)));
                // Assert at the cancellation boundary, before teardown's waits can hide live holders.
                gate.AssertExited(cancellation.Message);
                if (OperatingSystem.IsWindows())
                {
                    var snapshot = ProcessCommandLines.Snapshot();
                    Assert.Null(snapshot.Failure);
                    var holders = AssemblyTempRootCleanupHolderDiagnostics.Describe(fixture.Root, () => snapshot);
                    Assert.DoesNotContain(holders.Holders, holder => holder.Match == "cwd");
                }
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
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            // Teardown used to replace a boundary assertion/timeout with Directory.Delete's
            // IOException. Retain every failure so the gate receipt names the first boundary.
            var cleanupFailures = new List<Exception>();
            await CaptureCleanup(() => { lifetime.Cancel(); return Task.CompletedTask; });
            await CaptureCleanup(() => Drain(run));
            await CaptureCleanup(gate.WaitForExit);
            await CaptureCleanup(() => { fixture.Dispose(); return Task.CompletedTask; });
            if (cleanupFailures.Count > 0)
                throw new AggregateException("JUnit fixture failed before or during cleanup.",
                    failure is null ? cleanupFailures : new[] { failure }.Concat(cleanupFailures));

            async Task CaptureCleanup(Func<Task> cleanup)
            {
                try { await cleanup(); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
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
        private string _childIdentity = "child=unobserved";
        private string _launcherIdentity = "launcher=unobserved";
        private bool _childExitObserved;
        private bool _launcherExitObserved;
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
                _childIdentity = DescribeIdentity(_child, "child");
                if (OperatingSystem.IsWindows())
                {
                    // cmd.exe owns the command working directory independently of pwsh. Pin it
                    // while pwsh is gated: cancellation must await both identities, and waiting
                    // for pwsh alone cannot prove that both working-directory holders exited.
                    var parent = WindowsNativeProcessInspection.ReadParentProcessId(_child.Id);
                    Assert.Equal(ProcessInspectionStatus.Available, parent.Status);
                    _launcher = Process.GetProcessById(Assert.IsType<int>(parent.ParentProcessId));
                    _ = _launcher.SafeHandle;
                    Assert.Equal("cmd", _launcher.ProcessName, ignoreCase: true);
                    _launcherIdentity = DescribeIdentity(_launcher, "launcher");
                }
                _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            }
            catch (TimeoutException) { throw new TimeoutException("Fixture child did not signal its ready event."); }
        }
        internal Task Release() => (_writer ?? throw new InvalidOperationException("Child ready event missing.")).WriteLineAsync("release");
        internal void AssertExited(string cancellationDetail)
        {
            Assert.NotNull(_child);
            _childExitObserved = _child.HasExited;
            if (OperatingSystem.IsWindows())
            {
                Assert.NotNull(_launcher);
                _launcherExitObserved = _launcher.HasExited;
            }
            Assert.True(_childExitObserved && (!OperatingSystem.IsWindows() || _launcherExitObserved),
                $"Cancellation returned before all fixture processes exited: {DescribeProcesses()}; cancellation={cancellationDetail}");
        }
        internal async Task WaitForExit()
        {
            try
            {
                await WaitForExit(_child, "Fixture child");
                _childExitObserved = _child is not null;
                await WaitForExit(_launcher, "Fixture command launcher");
                _launcherExitObserved = _launcher is not null;
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
            catch (TimeoutException)
            {
                throw new TimeoutException($"{eventName} did not exit before directory cleanup: {DescribeIdentity(process, "pending")}");
            }
            Assert.True(process.HasExited);
        }
        private void DisposeProcesses()
        {
            _launcher?.Dispose();
            _launcher = null;
            _child?.Dispose();
            _child = null;
        }
        internal string DescribeProcesses() =>
            $"{_childIdentity} child_exit_observed={_childExitObserved} child_handle_released={_child is null}; " +
            $"{_launcherIdentity} launcher_exit_observed={_launcherExitObserved} launcher_handle_released={_launcher is null}";

        private static string DescribeIdentity(Process process, string role) =>
            $"{role}_pid={process.Id} {role}_start_utc_ticks={process.StartTime.ToUniversalTime().Ticks} {role}_image={process.ProcessName}";

        public void Dispose() { DisposeProcesses(); _writer?.Dispose(); _pipe.Dispose(); }
    }
}
