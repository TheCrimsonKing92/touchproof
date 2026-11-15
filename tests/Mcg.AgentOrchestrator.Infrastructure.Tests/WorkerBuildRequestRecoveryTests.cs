using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32.SafeHandles;

// Parallel-safe: fixture-owned native groups and canonical stores, with event/phase-gated stimuli.
public sealed class WorkerBuildRequestRecoveryTests(ITestOutputHelper output)
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CleanupFailure_UnconfirmedTermination_NamesLiveWorkerDescendant()
    {
        var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var suffix = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-holder-ready-" + suffix);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-holder-release-" + suffix);
        var childPidPath = Path.Combine(fixture.Root, "descendant.pid");
        var childScript = Path.Combine(fixture.Root, "descendant.ps1");
        Process? worker = null;
        Process? descendant = null;
        SafeFileHandle? retainedJob = null;
        var disposeAttempted = false;
        try
        {
            File.WriteAllText(childScript, $$"""
                [IO.File]::WriteAllText('{{childPidPath.Replace("'", "''")}}', $PID.ToString())
                $ready = [Threading.EventWaitHandle]::OpenExisting('Local\mcg-holder-ready-{{suffix}}')
                $null = $ready.Set()
                $release = [Threading.EventWaitHandle]::OpenExisting('Local\mcg-holder-release-{{suffix}}')
                if (-not $release.WaitOne(240000)) { throw 'worker descendant release event did not arrive' }
                """);
            var start = new ProcessStartInfo(WorkerShell.Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root
            };
            // The worker synchronously invokes the child, so both remain live at cleanup.
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                "& '" + WorkerShell.Executable.Replace("'", "''") + "' -NoProfile -NonInteractive -File '" +
                childScript.Replace("'", "''") + "'" }) start.ArgumentList.Add(argument);
            fixture.Worker = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(start,
                Path.Combine(fixture.Root, "worker.out.log"), Path.Combine(fixture.Root, "worker.err.log"));
            worker = Process.GetProcessById(fixture.Worker.Process.Id);
            _ = worker.SafeHandle; // Hold this process identity through the finally-block stop.
            fixture.Worker.Resume();
            Assert.True(await Task.Run(() => ready.WaitOne(TestHangGuard.HangSafetyBound)),
                "Worker descendant did not signal its named readiness event.");
            var descendantId = int.Parse(File.ReadAllText(childPidPath), System.Globalization.CultureInfo.InvariantCulture);
            descendant = Process.GetProcessById(descendantId);
            _ = descendant.SafeHandle;
            var descendantName = descendant.ProcessName;
            Assert.False(worker.HasExited);
            Assert.False(descendant.HasExited);

            // Closing only the fixture's handle makes termination unconfirmed. The duplicate
            // retains kill-on-close custody if this test host dies before its finally block.
            Assert.True(fixture.Worker.Group.TryDuplicateAccountingHandle(out retainedJob));
            fixture.Worker.Group.Dispose();
            disposeAttempted = true;
            var error = await Task.Run(() => Assert.Throws<AggregateException>(fixture.Dispose));

            Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message ==
                "Owned worker/descendant termination is unconfirmed; retain fixture artifacts.");
            Assert.StartsWith($"Worker build fixture cleanup failed. Residual root '{fixture.Root}' attributes=", error.Message);
            Assert.Contains($"pid={descendantId} name={descendantName}", error.Message);
            Assert.False(worker.HasExited);
            Assert.False(descendant.HasExited);
        }
        finally
        {
            try { await StopExactProcess(descendant, "worker descendant exit after exact-pid termination"); }
            finally
            {
                try { await StopExactProcess(worker, "worker exit after exact-pid termination"); }
                finally
                {
                    retainedJob?.Dispose();
                    try { if (!disposeAttempted) fixture.Dispose(); }
                    finally { SharedTestSupport.RemoveTempDirectory(fixture.Root); }
                }
            }
        }

        static async Task StopExactProcess(Process? process, string exitEvent)
        {
            if (process is null) return;
            try
            {
                if (!process.HasExited) process.Kill();
                await TestHangGuard.WaitAsync(process.WaitForExitAsync(), exitEvent);
            }
            finally { process.Dispose(); }
        }
    }

    [Fact]
    public async Task ManagedHostDeathAfterCompilerStart_ReloadRetainsTerminalObligationWithoutRelaunch()
    {
        using var fixture = new WorkerBuildRequestFixture(managedHost: true, compilerMode: WorkerBuildRequestFixture.CompilerMode.Real);
        var suffix = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-build-ready-" + suffix);
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\mcg-build-release-" + suffix);
        var command = "\"" + WorkerShell.Executable + "\" -NoProfile -NonInteractive -Command \"" +
            "$r=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-build-ready-" + suffix + "');$null=$r.Set();" +
            "$g=[Threading.EventWaitHandle]::OpenExisting('Local\\mcg-build-release-" + suffix + "');" +
            "if(-not $g.WaitOne(240000)){throw 'named compiler release event did not arrive'}\"";
        var path = Path.Combine(fixture.RepositoryRoot, WorkerBuildRequestFixture.Project);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<Target Name=\"FixtureGate\" BeforeTargets=\"CoreCompile\"><Exec Command=\"" + SecurityElement.Escape(command) + "\" /></Target></Project>");
        Process? host = null;
        Process? compiler = null;
        SpawnProcessIdentity? hostIdentity = null;
        string? hostDiagnosticPath = null;
        var runner = new BackgroundDispatchRunner(disableProcessStart: false, startProcess: start =>
        {
            hostDiagnosticPath = DispatchProcessHost.ReadParameters(start.ArgumentList[^1]).HostDiagnosticPath;
            host = Process.Start(start);
            if (host is not null) hostIdentity = SpawnProcessIdentityReader.ReadForRegistration(host).Identity;
            return host;
        })
        { WorkerBuildInvocations = fixture.Operations };
        try
        {
            var started = runner.StartLatestDispatch(fixture.Kernel, fixture.GoalId, fixture.TaskId, Path.Combine(fixture.Root, "logs"),
                checkpointBeforeWorkerStart: (_, _, _, _) => fixture.Save(), taskIntentAdmission: fixture.Reference.Admission);
            Assert.NotNull(host);
            Assert.NotNull(hostIdentity);
            Assert.Equal(started.ProcessId, host.Id);
            var compilerReady = await Task.Run(() => ready.WaitOne(TimeSpan.FromMinutes(3)));
            if (!compilerReady) output.WriteLine(fixture.DescribeHostBuildDiagnostics(hostDiagnosticPath));
            Assert.True(compilerReady, "Owned compiler did not signal the named readiness event.");
            var before = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.Equal(WorkerBuildPhase.CompilerStarted, before.Phase);
            var child = Assert.Single(before.Children!);
            compiler = Process.GetProcessById(child.Pid);
            Assert.Equal(child, WorkerBuildRequestChannel.ReadIdentity(compiler));
            foreach (var foreign in new[] { hostIdentity with { ProcessId = Environment.ProcessId },
                hostIdentity with { StartedAt = hostIdentity.StartedAt.AddDays(-1) },
                hostIdentity with { ImagePath = hostIdentity.ImagePath + ".foreign" } })
                Assert.Throws<InvalidOperationException>(() =>
                    TestOwnedProcessStop.StopRootIfSame(host, foreign, TimeSpan.FromSeconds(30)));
            Assert.False(host.HasExited);
            Assert.False(compiler.HasExited);
            TestOwnedProcessStop.StopRootIfSame(host, hostIdentity, TimeSpan.FromSeconds(30));
            await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var restarted = new Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations(fixture.StatePath, fixture.Root, fixture.Storage);
            Assert.True(restarted.ReconcileOutstanding(fixture.Kernel, fixture.GoalId, fixture.TaskId));
            var terminal = Assert.Single(restarted.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
            Assert.Equal(before.Id, terminal.Id);
            Assert.Equal(WorkerBuildOutcome.Interrupted, terminal.Outcome);
            Assert.True(terminal.CustodyReleased);
            Assert.False(terminal.Consumed);
            Assert.Single(terminal.Children!);
            Assert.True(restarted.ReconcileOutstanding(fixture.Kernel, fixture.GoalId, fixture.TaskId));
            Assert.Equal(terminal.Version, Assert.Single(restarted.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Version);
        }
        finally
        {
            try
            {
                if (host is not null)
                {
                    Assert.NotNull(hostIdentity);
                    TestOwnedProcessStop.StopRootIfSame(host, hostIdentity, TimeSpan.FromSeconds(20));
                }
                // Confirm owned custody reaped every recorded compiler before opening its
                // gate, including assertion/setup failures before the observer was opened.
                if (compiler is not null)
                    await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                foreach (var invocation in fixture.Operations.Load(fixture.GoalId.Value)
                    .GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations)
                    Assert.All(invocation.Children ?? [], child => Assert.False(
                        Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations.IsExactProcessAlive(child),
                        "Host death did not reap its owned compiler; the release event remains closed."));
                // The original host handle is still held, preventing PID reuse. Release
                // only its matching registration, after the compiler has exited via custody.
                if (hostIdentity is not null && WorkerProcessJobs.TryGetRegisteredIdentity(hostIdentity.ProcessId, out var registered))
                {
                    Assert.Equal(hostIdentity, registered);
                    WorkerProcessJobs.Release(hostIdentity.ProcessId);
                }
                release.Set();
            }
            catch { fixture.PreserveArtifacts = true; throw; }
            finally { compiler?.Dispose(); host?.Dispose(); }
        }
    }

    [Theory]
    [InlineData(WorkerBuildPhase.Authorized, false)]
    [InlineData(WorkerBuildPhase.CompilerStarted, true)]
    public async Task OperatorIntentCancellationBeforeAndAfterStartIsAttributable(WorkerBuildPhase cancelAt, bool started)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.FakeUntilStopped);
        Task? intake = null;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase != cancelAt || intake is not null) return;
            void ApplyIntent()
            {
                var store = new SqliteOperatorIntentStore(fixture.Reference.Admission!.StorePath, fixture.Root);
                var intent = store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                    OperatorIntentVerbs.CancelDispatch, fixture.GoalId.Value, fixture.TaskId.Value, "{}", [],
                    "fixture", "test", "local-process", DateTimeOffset.UtcNow)).GetAwaiter().GetResult();
                // Intake alone now defers a running dispatch. Resolve the intent through the
                // store lifecycle so this test still exercises Applied-intent cancellation.
                const string owner = "recovery-test";
                var claimed = store.ClaimNextAsync(fixture.GoalId.Value, owner).GetAwaiter().GetResult();
                Assert.Equal(intent.Id, claimed?.Id);
                store.CompleteAsync(intent.Id, owner, OperatorIntentStatus.Applied, "applied", DateTimeOffset.UtcNow)
                    .GetAwaiter().GetResult();
            }
            if (started) intake = Task.Run(ApplyIntent); // the resume callback still owns the task admission mutex
            else { ApplyIntent(); intake = Task.CompletedTask; }
        });
        Assert.NotNull(intake);
        await intake.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(WorkerBuildOutcome.Cancelled, invocation.Outcome);
        Assert.Equal(started, invocation.Observations.Any(o => o.Phase == WorkerBuildPhase.CompilerStarted));
        Assert.True(invocation.CustodyReleased);
        var authority = new SqliteOperatorIntentStore(fixture.Reference.Admission!.StorePath, fixture.Root);
        var suppression = Assert.IsType<TaskIntentSuppressionReceipt>(
            authority.GetSuppression("worker-build-lifetime:" + invocation.Id));
        Assert.Equal(OperatorIntentStatus.Applied.ToString(), suppression.IntentStatus);
        Assert.Equal("task-admission-version-changed", suppression.Reason);
    }

    [Theory]
    [InlineData(WorkerBuildPhase.Authorized, false)]
    [InlineData(WorkerBuildPhase.CompilerStarted, true)]
    public async Task WorkerExitCancelsOutstandingInvocationAndConfirmsOwnedTermination(WorkerBuildPhase exitAt, bool compilerStarted)
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.FakeUntilStopped);
        var exitRequested = false;
        var invocation = await fixture.RunHelper(i =>
        {
            if (i.Phase == exitAt && !exitRequested)
            {
                exitRequested = true;
                fixture.Worker!.Group.Kill();
            }
        }, assertAdapterExit: false);
        Assert.True(exitRequested);
        Assert.Equal(WorkerBuildOutcome.Cancelled, invocation.Outcome);
        Assert.Equal(compilerStarted ? 1 : 0, invocation.Children?.Count ?? 0);
        Assert.True(invocation.CustodyReleased);
        Assert.All(invocation.Children ?? [], child => Assert.False(
            Mcg.AgentOrchestrator.App.Orchestration.WorkerBuildInvocationOperations.IsExactProcessAlive(child)));
    }

    [Fact]
    public async Task ReloadAndDuplicateSubmissionDoNotLaunchOrConsumeTwice()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        var reopened = fixture.Host();
        var replay = await reopened.ExecuteAsync(new WorkerBuildRequest(1, invocation.RequestKey,
            invocation.Projects.ToArray(), invocation.Configuration), invocation.Worker, CancellationToken.None);
        Assert.Equal(invocation.Id, replay.InvocationId);
        Assert.Equal(WorkerBuildOutcome.Success, replay.Outcome);
        Assert.Single(Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Children!);
        Assert.False(fixture.Consume().MissingEvidence);
        var applied = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.True(applied.Consumed);
        Assert.False(fixture.Consume().MissingEvidence);
        Assert.Equal(applied.Version, Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations).Version);
    }

    [Fact]
    public async Task DuplicateKeyWithChangedPayloadCannotLaunchAnAdditionalChild()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var invocation = await fixture.RunHelper();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Host().ExecuteAsync(
            new WorkerBuildRequest(1, invocation.RequestKey, invocation.Projects.ToArray(), "Release"),
            invocation.Worker, CancellationToken.None));
        Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
    }

    [Fact]
    public async Task StaleSnapshotCannotDropTerminalOutcomeOrConsumedState()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake);
        var repository = new SqliteOrchestratorStateRepository(fixture.StatePath);
        var stale = await repository.LoadGoalsAsync([fixture.GoalId]);
        var before = stale.ExportGoalSnapshot(fixture.GoalId);
        var invocation = await fixture.RunHelper();
        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.False(fixture.Consume().MissingEvidence);
        var result = await repository.SaveGoalSnapshotsWithMergeAsync([new GoalSnapshotSaveRequest(before, stale.ExportGoalSnapshot(fixture.GoalId))]);
        Assert.NotEmpty(result);
        var restored = Assert.Single(fixture.Operations.Load(fixture.GoalId.Value).GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildInvocations);
        Assert.Equal(WorkerBuildOutcome.Success, restored.Outcome);
        Assert.True(restored.CustodyReleased);
        Assert.True(restored.Consumed);
        Assert.Equal(0, fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).WorkerBuildCheckRecoveryCount);
    }
}
