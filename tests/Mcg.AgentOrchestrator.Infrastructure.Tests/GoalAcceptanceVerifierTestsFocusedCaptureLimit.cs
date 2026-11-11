using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsFocusedCaptureLimit : GoalAcceptanceVerifierTestBase
{
    private const long CapBytes = 4096;
    private static readonly TimeSpan ApparatusHangFailsafe = TimeSpan.FromSeconds(20);

    [Xunit.Fact]
    public async Task FocusedCommandStopsAtCaptureLimitAndReapsItsChild()
    {
        var root = CreateRoot();
        var owner = CreateFocusedOwner(root);
        var pidPath = Path.Combine(root, "child.pid");
        var check = LoopingCheck(timeoutMinutes: 30, pidPath);
        try
        {
            var verifier = new GoalAcceptanceVerifier(new CrosswalkExecutableResolver());
            var run = verifier.RunInvocationForOwnerTestsAsync(
                owner, owner.CreateInvocation(check.Name), check, Path.GetTempPath(), owner.CancellationToken);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(result.Result.Passed);
            Assert.StartsWith("acceptance-check-capture-limit:", result.Result.Name, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance-check-timeout", result.Result.Name, StringComparison.Ordinal);
            AssertProcessExited(int.Parse(File.ReadAllText(pidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            var captureDecision = GoalAcceptanceVerifier.InferPartitionCompletionDecisionForTests(result.Result);
            var timeoutDecision = GoalAcceptanceVerifier.InferPartitionCompletionDecisionForTests(
                result.Result with { Name = result.Result.Name.Replace(
                    "acceptance-check-capture-limit:", "acceptance-check-timeout:", StringComparison.Ordinal) });
            Assert.Equal(AcceptanceShardCompletionPredicates.TimedOut, captureDecision.FailedPredicate);
            Assert.Equal(timeoutDecision.FailedPredicate, captureDecision.FailedPredicate);
            Assert.Equal(timeoutDecision.TimedOut, captureDecision.TimedOut);
        }
        finally
        {
            owner.Cancel();
            await owner.DisposeAsync();
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedProcessMarkerIsDistinctAndChildIsExited()
    {
        var root = CreateRoot();
        using var cancellation = new CancellationTokenSource();
        var captureObservation = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        GoalAcceptanceVerifier.CommandResult? result = null;
        Task<GoalAcceptanceVerifier.CommandResult>? run = null;
        try
        {
            run = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                LoopingArguments(), Path.GetTempPath(), TimeSpan.FromMinutes(30), CapBytes,
                GateHeartbeatRunClass.FocusedEvidence, cancellationToken: cancellation.Token,
                captureObserver: (_, _, reached) => captureObservation.TrySetResult(reached));
            result = await run.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(result.CaptureLimited);
            Assert.True(captureObservation.Task.IsCompletedSuccessfully);
            Assert.True(captureObservation.Task.Result.IsCompletedSuccessfully);
            Assert.False(result.TimedOut);
            Assert.Equal(CapBytes, result.CaptureLimitBytes);
            Assert.NotNull(result.ChildProcessId);
            AssertProcessExited(result.ChildProcessId!.Value);
            Assert.Equal(1, CountOccurrences(result.Output, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
        }
        finally
        {
            await cancellation.CancelAsync();
            if (run is not null) try { await run.WaitAsync(TimeSpan.FromSeconds(15)); } catch { }
            DeleteCaptureFiles(result);
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public Task FullGateCommandStillTimesOutAfterCaptureLimit() => RunFullGateCaptureScenarioAsync();

    [Xunit.Fact]
    public async Task ApparatusFailsafeFailsBeforeTimeoutAndReapsItsChild()
    {
        var failure = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            RunFullGateCaptureScenarioAsync(expireApparatusFailsafe: true));
        Assert.Contains("Apparatus hang failsafe: real drain capture-limit observation", failure.Message, StringComparison.Ordinal);
        Assert.Contains("timeout_signaled=False", failure.Message, StringComparison.Ordinal);
        Assert.Contains("owned_child_reaped=True", failure.Message, StringComparison.Ordinal);
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() =>
            AwaitApparatusEventAsync(Task.CompletedTask, expired.Token, "already completed apparatus signal"));
    }

    private static async Task RunFullGateCaptureScenarioAsync(bool expireApparatusFailsafe = false)
    {
        var root = CreateRoot();
        using var cancellation = new CancellationTokenSource();
        using var timeoutSignal = new CancellationTokenSource();
        using var apparatusFailsafe = new CancellationTokenSource(ApparatusHangFailsafe);
        var captures = new TaskCompletionSource<(string Stdout, string Stderr, Task LimitReached)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new TaskCompletionSource<SpawnProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new ConcurrentQueue<AcceptanceProcessCleanupObservation>();
        var elapsed = Stopwatch.StartNew();
        GoalAcceptanceVerifier.CommandResult? processResult = null;
        Task<GoalAcceptanceVerifier.CommandResult>? processRun = null;
        Task? invocationRun = null;
        Exception? failure = null;
        string? diagnostics = null;
        await using var owner = CreateGateOwner(root);
        try
        {
            var verifier = new GoalAcceptanceVerifier(async (arguments, workingDirectory, _, cancellationToken) =>
            {
                processRun = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                    arguments, workingDirectory, TimeSpan.FromSeconds(3), CapBytes,
                    GateHeartbeatRunClass.Acceptance, commandIdentityObserver: value => identity.TrySetResult(value),
                    cancellationToken: cancellationToken, timeoutSignal: timeoutSignal.Token,
                    captureObserver: (stdout, stderr, limitReached) => captures.TrySetResult((stdout, stderr, limitReached)),
                    cleanupObserver: cleanup.Enqueue);
                processResult = await processRun;
                return processResult;
            });
            var check = expireApparatusFailsafe ? SilentCheck() : LoopingCheck(timeoutMinutes: 1);
            var run = owner.RunInvocationForTestsAsync(
                verifier, owner.CreateInvocation(check.Name), check, Path.GetTempPath(), cancellation.Token);
            invocationRun = run;
            await AwaitApparatusEventAsync(identity.Task, apparatusFailsafe.Token, "owned command identity");
            await AwaitApparatusEventAsync(captures.Task, apparatusFailsafe.Token, "capture observation setup");
            var capture = await captures.Task;
            // The negative control expires the failsafe by signal, never by waiting on a clock.
            if (expireApparatusFailsafe) apparatusFailsafe.Cancel();
            await AwaitApparatusEventAsync(capture.LimitReached, apparatusFailsafe.Token,
                "real drain capture-limit observation");
            Assert.False(processRun!.IsCompleted, "Crossing the cap must leave the full-gate process running.");
            Assert.False(run.IsCompleted, "Crossing the cap must leave the full-gate invocation running.");
            var child = await identity.Task;
            using (var process = Process.GetProcessById(child.ProcessId))
            {
                Assert.Equal(child.StartedAt, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                Assert.False(process.HasExited);
            }
            Assert.False(timeoutSignal.IsCancellationRequested);
            timeoutSignal.Cancel();
            await AwaitApparatusEventAsync(run, apparatusFailsafe.Token, "timeout result and owned-child cleanup");
            var result = await run;

            Assert.False(result.Result.Passed);
            Assert.StartsWith("acceptance-check-timeout:", result.Result.Name, StringComparison.Ordinal);
            Assert.NotNull(processResult);
            Assert.True(processResult!.TimedOut);
            Assert.False(processResult.CaptureLimited);
            Assert.Equal(-1, processResult.ExitCode);
            Assert.Equal(TimeSpan.FromSeconds(3), processResult.Timeout);
            Assert.Contains($"ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes={CapBytes} ",
                processResult.Output, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(processResult.Output, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));
            Assert.NotNull(processResult.ChildProcessId);
            AssertProcessExited(processResult.ChildProcessId.Value);
            Assert.Contains(cleanup, item => item.Stage == "owned-child-disposed" && item.ProcessDisposed);
        }
        catch (Exception exception)
        {
            failure = exception;
            // Snapshot live facts before cancellation, process reaping, or capture deletion.
            diagnostics = CaptureFailureDiagnostics(captures.Task, identity.Task, processResult,
                processRun, elapsed.Elapsed, timeoutSignal.IsCancellationRequested);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (processRun is not null)
            {
                try { processResult = await processRun.WaitAsync(ApparatusHangFailsafe); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { failure ??= exception; }
            }
            if (invocationRun is not null)
            {
                try { await invocationRun.WaitAsync(ApparatusHangFailsafe); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { failure ??= exception; }
            }
            DeleteCaptureFiles(processResult);
            try
            {
                if (captures.Task.IsCompletedSuccessfully)
                {
                    File.Delete(captures.Task.Result.Stdout);
                    File.Delete(captures.Task.Result.Stderr);
                }
                DeleteDirectoryWithRetry(root);
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null)
        {
            var reaped = cleanup.Any(item => item.Stage == "owned-child-disposed" && item.ProcessDisposed &&
                !item.RegistryActive && !item.NativeHandleOpen);
            try
            {
                if (identity.Task.IsCompletedSuccessfully) AssertProcessExited(identity.Task.Result.ProcessId);
                else reaped = false;
            }
            catch (Exception exception)
            {
                reaped = false;
                diagnostics += $"\nchild_reaping_failure={exception}";
            }
            throw new Xunit.Sdk.XunitException($"{failure}\n{diagnostics}\nowned_child_reaped={reaped}");
        }
    }

    [Xunit.Fact]
    public async Task CaptureObservationIsIsolatedAcrossConcurrentAndSequentialRuns()
    {
        using var cancellation = new CancellationTokenSource();
        using var noisyTimeout = new CancellationTokenSource();
        using var silentTimeout = new CancellationTokenSource();
        using var apparatusFailsafe = new CancellationTokenSource(ApparatusHangFailsafe);
        var noisyCapture = new TaskCompletionSource<(string Stdout, string Stderr, Task LimitReached)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var silentCapture = new TaskCompletionSource<(string Stdout, string Stderr, Task LimitReached)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCapture = new TaskCompletionSource<(string Stdout, string Stderr, Task LimitReached)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var silentIdentity = new TaskCompletionSource<SpawnProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var noisyIdentity = new TaskCompletionSource<SpawnProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextIdentity = new TaskCompletionSource<SpawnProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = Stopwatch.StartNew();
        var runs = new List<Task<GoalAcceptanceVerifier.CommandResult>>();
        Exception? failure = null;
        string? diagnostics = null;
        try
        {
            var silentRun = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                ["powershell", .. SilentCheck().Arguments!], Path.GetTempPath(), TimeSpan.FromMinutes(1), CapBytes,
                GateHeartbeatRunClass.Acceptance, commandIdentityObserver: value => silentIdentity.TrySetResult(value),
                cancellationToken: cancellation.Token, timeoutSignal: silentTimeout.Token,
                captureObserver: (stdout, stderr, reached) => silentCapture.TrySetResult((stdout, stderr, reached)));
            runs.Add(silentRun);
            await AwaitApparatusEventAsync(silentIdentity.Task, apparatusFailsafe.Token, "silent command identity");
            var noisyRun = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                LoopingArguments(), Path.GetTempPath(), TimeSpan.FromMinutes(1), CapBytes,
                GateHeartbeatRunClass.Acceptance, commandIdentityObserver: value => noisyIdentity.TrySetResult(value),
                cancellationToken: cancellation.Token, timeoutSignal: noisyTimeout.Token,
                captureObserver: (stdout, stderr, reached) => noisyCapture.TrySetResult((stdout, stderr, reached)));
            runs.Add(noisyRun);
            await AwaitApparatusEventAsync(noisyCapture.Task, apparatusFailsafe.Token, "noisy capture observation setup");
            var noisy = await noisyCapture.Task;
            var silent = await silentCapture.Task;
            await AwaitApparatusEventAsync(noisy.LimitReached, apparatusFailsafe.Token, "noisy real drain cap observation");
            Assert.NotSame(noisy.LimitReached, silent.LimitReached);
            Assert.False(silent.LimitReached.IsCompleted);
            Assert.False(noisyRun.IsCompleted);
            Assert.False(silentRun.IsCompleted);
            noisyTimeout.Cancel();
            await AwaitApparatusEventAsync(noisyRun, apparatusFailsafe.Token, "noisy timeout cleanup");
            Assert.True((await noisyRun).TimedOut);
            Assert.False(silent.LimitReached.IsCompleted);
            silentTimeout.Cancel();
            await AwaitApparatusEventAsync(silentRun, apparatusFailsafe.Token, "pre-cap timeout cleanup");
            var silentResult = await silentRun;
            Assert.True(silentResult.TimedOut);
            Assert.False(silentResult.CaptureLimited);
            Assert.Equal(0, CountOccurrences(silentResult.Output, "ACCEPTANCE_CAPTURE_LIMIT_REACHED"));

            var nextRun = GoalAcceptanceVerifier.RunProcessWithCaptureLimitForTestsAsync(
                ["powershell", "-NoProfile", "-NonInteractive", "-Command", "[Console]::Write('ok')"],
                Path.GetTempPath(), TimeSpan.FromMinutes(1), CapBytes, GateHeartbeatRunClass.Acceptance,
                commandIdentityObserver: value => nextIdentity.TrySetResult(value), cancellationToken: cancellation.Token,
                captureObserver: (stdout, stderr, reached) => nextCapture.TrySetResult((stdout, stderr, reached)));
            runs.Add(nextRun);
            await AwaitApparatusEventAsync(nextRun, apparatusFailsafe.Token, "sequential under-cap result");
            Assert.False((await nextCapture.Task).LimitReached.IsCompleted);
            Assert.NotSame(noisy.LimitReached, nextCapture.Task.Result.LimitReached);
            var nextResult = await nextRun;
            Assert.Equal(0, nextResult.ExitCode);
            Assert.False(nextResult.TimedOut);
            Assert.False(nextResult.CaptureLimited);
            Assert.Equal("ok", nextResult.Output);
        }
        catch (Exception exception)
        {
            failure = exception;
            diagnostics = CaptureFailureDiagnostics(noisyCapture.Task, noisyIdentity.Task,
                runs.Count > 1 && runs[1].IsCompletedSuccessfully ? runs[1].Result : null,
                runs.Count > 1 ? runs[1] : null, elapsed.Elapsed, noisyTimeout.IsCancellationRequested) + "\n" +
                CaptureFailureDiagnostics(silentCapture.Task, silentIdentity.Task,
                    runs.Count > 0 && runs[0].IsCompletedSuccessfully ? runs[0].Result : null,
                    runs.Count > 0 ? runs[0] : null, elapsed.Elapsed, silentTimeout.IsCancellationRequested) + "\n" +
                CaptureFailureDiagnostics(nextCapture.Task, nextIdentity.Task,
                    runs.Count > 2 && runs[2].IsCompletedSuccessfully ? runs[2].Result : null,
                    runs.Count > 2 ? runs[2] : null, elapsed.Elapsed, false);
        }
        finally
        {
            await cancellation.CancelAsync();
            foreach (var run in runs)
            {
                try { DeleteCaptureFiles(await run.WaitAsync(ApparatusHangFailsafe)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception) { failure ??= exception; }
            }
            try
            {
                foreach (var capture in new[] { noisyCapture, silentCapture, nextCapture })
                    if (capture.Task.IsCompletedSuccessfully)
                    {
                        File.Delete(capture.Task.Result.Stdout);
                        File.Delete(capture.Task.Result.Stderr);
                    }
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null) throw new Xunit.Sdk.XunitException($"{failure}\n{diagnostics}");
    }

    [Xunit.Fact]
    public async Task FocusedCommandUnderCapKeepsItsOrdinaryResultName()
    {
        var root = CreateRoot();
        await using var owner = CreateFocusedOwner(root);
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "small focused command", Type = "command", Command = "powershell",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('ok')"],
            TimeoutMinutes = 1
        };
        try
        {
            var verifier = new GoalAcceptanceVerifier(new CrosswalkExecutableResolver());
            var run = await verifier.RunInvocationForOwnerTestsAsync(
                owner, owner.CreateInvocation(check.Name), check, Path.GetTempPath(), owner.CancellationToken);

            Assert.True(run.Result.Passed);
            Assert.Equal(check.Name, run.Result.Name);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact]
    public async Task FocusedEvidenceDetailNamesCaptureLimitAndCapBytes()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        try
        {
            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["engine"]!["outputCaptureLimitBytes"] = CapBytes;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var verifier = new GoalAcceptanceVerifier((arguments, _, _, _) =>
                Task.FromResult(arguments.Length > 1 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? new GoalAcceptanceVerifier.CommandResult(
                        -1, "ACCEPTANCE_CAPTURE_LIMIT_REACHED cap_bytes=4096",
                        CaptureLimited: true, CaptureLimitBytes: CapBytes)
                    : new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.")));
            var result = await verifier.RunFocusedEvidenceAsync(
                root, null, "Infrastructure.Tests: FullyQualifiedName~GoalAcceptanceVerifierTestsFocusedCaptureLimit");

            Assert.True(result.Accepted);
            Assert.False(result.Passed);
            Assert.Contains("acceptance-check-capture-limit:", result.Summary, StringComparison.Ordinal);
            Assert.Contains("cap_bytes=4096", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance-check-timeout", result.Summary, StringComparison.Ordinal);

            var eventPath = Path.Combine(root, "conduct-events.log");
            var goal = new AgentOrchestratorKernel().CreateGoal("Record capture-limited focused evidence");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, []);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                runInline: true,
                acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(eventPath));
            coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "run focused tests",
                (_, _, _, _) => result);

            var evidenceEnd = Assert.Single(File.ReadLines(eventPath)
                .Select(line => JsonNode.Parse(line)!.AsObject())
                .Where(item => item["eventKind"]!.GetValue<string>() == "EVIDENCE_END"));
            var detail = evidenceEnd["detail"]!.GetValue<string>();
            Assert.Contains("acceptance-check-capture-limit:", detail, StringComparison.Ordinal);
            Assert.Contains("cap_bytes=4096", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance-check-timeout", detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static async Task AwaitApparatusEventAsync(Task signal, CancellationToken failsafe, string missingEvent)
    {
        try
        {
            failsafe.ThrowIfCancellationRequested();
            await signal.WaitAsync(failsafe);
            failsafe.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (failsafe.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException($"Apparatus hang failsafe: {missingEvent}");
        }
    }

    private static string CaptureFailureDiagnostics(
        Task<(string Stdout, string Stderr, Task LimitReached)> captures,
        Task<SpawnProcessIdentity> identity,
        GoalAcceptanceVerifier.CommandResult? result,
        Task? run,
        TimeSpan elapsed,
        bool timeoutSignaled)
    {
        var capture = captures.IsCompletedSuccessfully ? captures.Result : default;
        return $"capture_diagnostics elapsed={elapsed} child=" +
            (identity.IsCompletedSuccessfully ? identity.Result.ToString() : "unobserved") +
            $" cap_bytes={CapBytes} cap_observed={capture.LimitReached?.IsCompletedSuccessfully == true}" +
            $" timeout_signaled={timeoutSignaled} run_status={run?.Status.ToString() ?? "not-started"}\n" +
            $"stdout={CaptureFileSnapshot(capture.Stdout)}\nstderr={CaptureFileSnapshot(capture.Stderr)}\n" +
            $"result_timeout={result?.TimedOut} result_capture_limited={result?.CaptureLimited}" +
            $" result_stdout_bytes={result?.StdoutBytes} result_stderr_bytes={result?.StderrBytes}" +
            $" result_output={BoundDiagnosticOutput(result?.Output)}";
    }

    private static string CaptureFileSnapshot(string? path)
    {
        if (path is null) return "path-unobserved";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytes = stream.Length;
            var buffer = new byte[GoalAcceptanceVerifier.CappedOutputPreviewBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = stream.Read(buffer, read, buffer.Length - read);
                if (count == 0) break;
                read += count;
            }
            return $"path={path} bytes={bytes} preview={Encoding.UTF8.GetString(buffer, 0, read)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"path={path} unavailable={exception.GetType().Name}:{exception.Message}";
        }
    }

    private static string? BoundDiagnosticOutput(string? output) =>
        output?[..Math.Min(output.Length, GoalAcceptanceVerifier.CappedOutputPreviewBytes)];

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck SilentCheck() => new()
    {
        Name = "silent command", Type = "command", Command = "powershell",
        Arguments = ["-NoProfile", "-NonInteractive", "-Command",
            "$gate = [System.Threading.ManualResetEvent]::new($false); " +
            "try { [void]$gate.WaitOne() } finally { $gate.Dispose() }"],
        TimeoutMinutes = 1
    };

    private static string[] LoopingArguments() =>
        ["powershell", "-NoProfile", "-NonInteractive", "-Command",
            "while ($true) { [Console]::WriteLine('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"];

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck LoopingCheck(int timeoutMinutes, string? pidPath = null) => new()
    {
        Name = "unbounded output", Type = "command", Command = "powershell",
        Arguments = pidPath is null ? LoopingArguments()[1..] :
            ["-NoProfile", "-NonInteractive", "-Command",
                $"Set-Content -LiteralPath '{pidPath.Replace("'", "''", StringComparison.Ordinal)}' -Value $PID; " +
                "while ($true) { [Console]::WriteLine('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx') }"],
        TimeoutMinutes = timeoutMinutes
    };

    private static AcceptanceFocusedVerificationOwner CreateFocusedOwner(string root) => new(
        new AcceptanceFocusedVerificationIdentity("focused-capture-test", "goal", root, "candidate",
            Path.Combine(root, "focused-results"), null),
        new AcceptanceGateEngineSettings { OutputCaptureLimitBytes = CapBytes });

    private static AcceptanceAttemptExecutionOwner CreateGateOwner(string root) => new(
        new AcceptanceAttemptIdentity("gate-capture-test", "goal", root, "candidate", "main",
            "candidate", Path.Combine(root, "gate-results"), null, Environment.ProcessId, null),
        new AcceptanceGateEngineSettings { OutputCaptureLimitBytes = CapBytes });

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-focused-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
            // The OS has already released the child process identity.
        }
    }

    private static int CountOccurrences(string text, string token) =>
        (text.Length - text.Replace(token, "", StringComparison.Ordinal).Length) / token.Length;

    private static void DeleteCaptureFiles(GoalAcceptanceVerifier.CommandResult? result)
    {
        if (result?.StdoutPath is { } stdout) try { File.Delete(stdout); } catch { }
        if (result?.StderrPath is { } stderr) try { File.Delete(stderr); } catch { }
    }
}
