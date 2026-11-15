using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: token-aware substituted runner and fixture-local stores only.
public sealed class SelectedExecutionBackstopCancellationTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task RelaunchTimeout_LoopDrain_CancelsBackstopAndHandsOff()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = async (_, token) =>
        {
            try { return await release.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        await fixture.Land();
        await fixture.Service();
        Task? loopTask = null;
        try
        {
            await TestHangGuard.WaitAsync(fixture.Entered.Task, "relaunch drain backstop entered");
            var kernel = new AgentOrchestratorKernel();
            CreateVerifiedSimpleGoal(kernel, "Update conductor runtime");
            var startedAt = fixture.Now;
            var gateCounts = new List<int>();
            var handoffs = 0;
            var admissions = 0;
            var sleeps = 0;
            var loopOutput = string.Empty;
            loopTask = Task.Run(() => loopOutput = AsyncLocalConsoleRouter.Capture(() =>
                new ConductorBatchLoop(
                    utcNow: () => fixture.Now,
                    readRelaunchDrainCap: () => null,
                    countLiveGroupedGates: () =>
                    {
                        gateCounts.Add(fixture.Coordinator.RunningCount);
                        // Timeout defers the relaunch until a counted work tick.
                        // Queue work during the drain so that retry can advance.
                        if (gateCounts.Count == 1)
                            GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Queued during backstop drain");
                        return 0;
                    },
                    selfRelaunchEnabled: true,
                    selfRelaunch: _ =>
                    {
                        // Exit cleanup also cancels: require cancellation before handoff to
                        // distinguish the timeout path from that later cleanup path.
                        Assert.True(cancelled.Task.IsCompletedSuccessfully);
                        Assert.True(Assert.Single(fixture.Tokens).IsCancellationRequested);
                        Assert.Equal(0, fixture.Coordinator.RunningCount);
                        handoffs++;
                        return ConductorLiveGroupedGateCountTests.Handoff();
                    }) { SelectedExecutionBackstop = fixture.Coordinator }.Run(
                        kernel, ConductorLiveGroupedGateCountTests.LandingDriver(() => admissions++),
                        ConductorAutonomyPolicy.Conservative, Path.Combine(fixture.Home, "stop"),
                        maxIterations: 5, keepAliveWhenIdle: true, watchInterval: TimeSpan.FromSeconds(1),
                        sleepFunc: _ =>
                        {
                            Assert.True(++sleeps <= 3, "The cancelled backstop drain did not reach handoff.");
                            // The loop can sleep before entering the relaunch drain.
                            // Advance time only after its first live-gate census.
                            if (gateCounts.Count == 1)
                            {
                                Assert.Equal(1, fixture.Coordinator.RunningCount);
                                Assert.False(Assert.Single(fixture.Tokens).IsCancellationRequested);
                                fixture.Now = startedAt + DispatchRecoveryPolicy.DefaultLiveIdleTimeout + TimeSpan.FromSeconds(1);
                            }
                            return false;
                        })));
            await TestHangGuard.WaitAsync(loopTask, "timeout relaunch drain returned after cancellation");

            Assert.Equal(1, handoffs);
            Assert.Equal(1, admissions);
            Assert.Equal(new[] { 1, 1, 0 }, gateCounts);
            Assert.Single(fixture.Calls);
            var cancelLine = Assert.Single(fixture.Lines.Where(line => line.Contains("result=cancel-requested")));
            Assert.Contains("request=" + Assert.Single(fixture.Calls).Id, cancelLine);
            Assert.Contains("reason=relaunch-drain-timeout", cancelLine);
            Assert.Contains("active=0 admitting=false liveGates=1", loopOutput);
            Assert.Equal(1, CountOccurrences(loopOutput, "LOOP_RELAUNCH_ROLLBACK"));
            Assert.True(loopOutput.IndexOf("LOOP_RELAUNCH_ROLLBACK", StringComparison.Ordinal) <
                        loopOutput.IndexOf("LOOP_RELAUNCH_REBUILD", StringComparison.Ordinal));
            var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
            Assert.True(state.Request!.RetryPending);
            Assert.Null(state.OpenMiss);
        }
        finally
        {
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            try
            {
                if (loopTask is not null)
                    await TestHangGuard.WaitAsync(loopTask, "relaunch drain loop cleanup returned");
            }
            finally
            {
                await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "relaunch drain runner cleanup settled");
            }
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task RelaunchTimeout_GateCensus_CancelsOnlySoleBackstop(int otherGates, bool cancels)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = (_, token) => release.Task.WaitAsync(token);
        await fixture.Land();
        await fixture.Service();
        try
        {
            await TestHangGuard.WaitAsync(fixture.Entered.Task, "timeout backstop entered");
            var startedAt = fixture.Now;
            fixture.Now += DispatchRecoveryPolicy.DefaultLiveIdleTimeout;
            var counts = fixture.Coordinator.ResumeRelaunchDrain(() => otherGates);
            Assert.Equal((otherGates + 1, 1), counts);
            var hold = new ConductorSelfRelaunchDrainHold(false, counts.Live, fixture.Now - startedAt);
            Assert.True(hold.TimedOut);
            fixture.Coordinator.CancelForRelaunchDrainTimeout(counts.Live, counts.Backstop, 42);
            var token = Assert.Single(fixture.Tokens);
            Assert.Equal(cancels, token.IsCancellationRequested);
            var cancelLines = fixture.Lines.Where(line => line.Contains("result=cancel-requested")).ToArray();
            if (cancels)
            {
                await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "timeout backstop settled");
                var line = Assert.Single(cancelLines);
                Assert.Contains("request=" + Assert.Single(fixture.Calls).Id, line);
                Assert.Contains("reason=relaunch-drain-timeout tick=42", line);
                await fixture.Service();
                Assert.Single(fixture.Calls); // Timeout holds the infrastructure retry until the next drain.
                Assert.Equal((0, 0), fixture.Coordinator.ResumeRelaunchDrain(() => 0));
                fixture.Coordinator.CancelForRelaunchDrainTimeout(0, 0, 43);
                Assert.Single(fixture.Lines.Where(item => item.Contains("result=cancel-requested")));
            }
            else
            {
                Assert.Empty(cancelLines);
                Assert.Equal((otherGates + 1, 1), fixture.Coordinator.ResumeRelaunchDrain(() => otherGates));
            }
        }
        finally
        {
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            await TestHangGuard.WaitAsync(fixture.Coordinator.CancelAndDrainAsync(), "timeout runner cleanup");
        }
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopExit_RunningBackstop_CancelsBeforeDrain(bool exceptionalExit)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = (_, token) => release.Task.WaitAsync(token);
        await fixture.Land();
        await fixture.Service();
        Task? loopTask = null;
        try
        {
            await TestHangGuard.WaitAsync(fixture.Entered.Task, "loop exit backstop entered");
            var loopHome = Directory.CreateDirectory(Path.Combine(fixture.Home, "loop")).FullName;
            var stopPath = Path.Combine(loopHome, "stop");
            if (!exceptionalExit) File.WriteAllText(stopPath, "stop while backstop is live");
            BatchLoopSummary? summary = null;
            var sleepCalls = 0;
            loopTask = Task.Run(() => AsyncLocalConsoleRouter.Capture(() =>
                summary = new ConductorBatchLoop { SelectedExecutionBackstop = fixture.Coordinator }.Run(
                    new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative, stopPath,
                    keepAliveWhenIdle: true, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ =>
                    {
                        sleepCalls++;
                        throw new InvalidOperationException("test-loop-exit");
                    })));
            if (exceptionalExit)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    TestHangGuard.WaitAsync(loopTask, "exceptional loop exit returned"));
                Assert.Equal("test-loop-exit", error.Message);
            }
            else
            {
                await TestHangGuard.WaitAsync(loopTask, "stopped loop returned");
                Assert.True(Assert.IsType<BatchLoopSummary>(summary).StopRequested);
            }
            Assert.Equal(exceptionalExit ? 1 : 0, sleepCalls);
            Assert.True(Assert.Single(fixture.Tokens).IsCancellationRequested);
            Assert.Equal(0, fixture.Coordinator.RunningCount);
            Assert.Equal(Assert.Single(fixture.Calls).Id, (await fixture.Store.ReadAsync())!.Request!.Id);
            Assert.True((await fixture.Store.ReadAsync())!.Request!.RetryPending);
            Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        }
        finally
        {
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            if (loopTask is not null)
            {
                // Observe the expected exceptional exit again without hiding a stuck cleanup.
                try { await TestHangGuard.WaitAsync(loopTask, "loop exit cleanup returned"); }
                catch (InvalidOperationException ex) when (exceptionalExit && ex.Message == "test-loop-exit") { }
            }
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "loop exit runner cleanup");
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Running_Cancel_RequeuesWithoutVerdict()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = (_, token) => release.Task.WaitAsync(token);
        await fixture.Land();
        await fixture.Service();
        try
        {
            await TestHangGuard.WaitAsync(fixture.Entered.Task, "backstop runner entered");
            var request = Assert.Single(fixture.Calls);
            var token = Assert.Single(fixture.Tokens);
            Assert.True(token.CanBeCanceled);
            Assert.False(token.IsCancellationRequested);
            Assert.Equal(request.Id, fixture.Coordinator.CancelRunning());
            Assert.True(token.IsCancellationRequested);
            Assert.Null(fixture.Coordinator.CancelRunning());
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "cancelled backstop settled");

            var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
            var pending = Assert.IsType<SelectedExecutionBackstopRequest>(state.Request);
            Assert.Equal(request.Id, pending.Id);
            Assert.True(pending.RetryPending);
            Assert.Equal(1, pending.Attempts);
            Assert.Null(state.OpenMiss);
            Assert.Null(state.CoveredCommit);
            Assert.Single(state.Uncovered);
            Assert.Empty(await fixture.Items.ListAsync());
            var events = await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType);
            Assert.Single(events.Where(evt => evt.Operation == "infrastructure-failure"));
            Assert.DoesNotContain(events, evt => evt.Operation is "red" or "green");
            Assert.Contains(fixture.Lines, line => line.Contains("result=infrastructure-failure"));
            Assert.DoesNotContain(fixture.Lines, line => line.Contains("result=red") || line.Contains("result=green"));
            Assert.Equal(0, fixture.Coordinator.RunningCount);
            Assert.Null(fixture.Coordinator.CancelRunning());
        }
        finally
        {
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "backstop cleanup settled");
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Running_Cancel_KeepsGateUntilRunnerSettles()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            ready.SetResult();
            await settle.Task;
            token.ThrowIfCancellationRequested();
            return SelectedExecutionBackstopOutcome.Green;
        };
        await fixture.Land();
        await fixture.Service();
        try
        {
            await TestHangGuard.WaitAsync(ready.Task, "runner registered cancellation");
            Assert.Equal(Assert.Single(fixture.Calls).Id, fixture.Coordinator.CancelRunning());
            await TestHangGuard.WaitAsync(cancelled.Task, "runner observed cancellation");
            // The runner cannot return until this test releases settle.
            Assert.Equal(1, fixture.Coordinator.RunningCount);
            Assert.NotNull(fixture.Coordinator.ActiveRequestId);
            settle.SetResult();
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "cancelled runner finished settling");
            Assert.Equal(0, fixture.Coordinator.RunningCount);
            Assert.Null(fixture.Coordinator.ActiveRequestId);
        }
        finally
        {
            settle.TrySetResult();
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "settling runner cleanup");
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task IdleAndCompleted_Cancel_PreservesGreen()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        Assert.Null(fixture.Coordinator.CancelRunning());
        await fixture.Land();
        await fixture.Finish();
        var token = Assert.Single(fixture.Tokens);
        Assert.True(token.CanBeCanceled);
        Assert.False(token.IsCancellationRequested);
        var before = await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType);
        Assert.Null(fixture.Coordinator.CancelRunning());
        Assert.Null(fixture.Coordinator.CancelRunning());
        var state = Assert.IsType<SelectedExecutionBackstopState>(await fixture.Store.ReadAsync());
        Assert.Equal(fixture.Head, state.CoveredCommit);
        Assert.Null(state.Request);
        Assert.Null(state.OpenMiss);
        var after = await fixture.Events.ReadByTypeSinceAsync(SelectedExecutionBackstopStore.EventType);
        Assert.Equal(before.Count, after.Count);
        Assert.Single(after.Where(evt => evt.Operation == "green"));
        Assert.DoesNotContain(after, evt => evt.Operation is "red" or "infrastructure-failure");
        Assert.False(token.IsCancellationRequested);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CancelWithHold_Service_WaitsForReleaseAndUsesFreshToken()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = (_, token) => release.Task.WaitAsync(token);
        await fixture.Land();
        await fixture.Service();
        try
        {
            await TestHangGuard.WaitAsync(fixture.Entered.Task, "held backstop runner entered");
            Assert.Equal(Assert.Single(fixture.Calls).Id, fixture.Coordinator.CancelRunning(holdRestart: true));
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "held cancelled backstop settled");
            var pending = (await fixture.Store.ReadAsync())!.Request!;
            await fixture.Land("goal-2", "main-3");
            fixture.Head = "main-3";
            await fixture.Service();
            Assert.Single(fixture.Calls);
            var held = (await fixture.Store.ReadAsync())!.Request!;
            Assert.Equal(pending.ToCommit, held.ToCommit);
            Assert.Equal(pending.ThroughOrdinal, held.ThroughOrdinal);
            Assert.True(held.RetryPending);

            fixture.Coordinator.ReleaseRestartHold();
            fixture.RunWithCancellation = (_, _) => Task.FromResult(SelectedExecutionBackstopOutcome.Green);
            await fixture.Finish();
            var tokens = fixture.Tokens.ToArray();
            Assert.Equal(2, tokens.Length);
            Assert.True(tokens[0].IsCancellationRequested);
            Assert.True(tokens[1].CanBeCanceled);
            Assert.False(tokens[1].IsCancellationRequested);
            Assert.NotEqual(tokens[0], tokens[1]);
            var calls = fixture.Calls.ToArray();
            Assert.Equal(2, calls.Length);
            Assert.Equal(pending.Id, calls[1].Id);
            Assert.Equal(2, calls[1].Attempts);
            Assert.Equal("main-3", calls[1].ToCommit);
            Assert.Equal("main-3", (await fixture.Store.ReadAsync())!.CoveredCommit);
        }
        finally
        {
            fixture.Coordinator.ReleaseRestartHold();
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "held backstop cleanup settled");
        }
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task CancellationCallback_Throws_CancelStillSettles()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SelectedExecutionBackstopOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RunWithCancellation = async (_, token) =>
        {
            var outcome = release.Task.WaitAsync(token);
            using var registration = token.Register(() =>
            {
                callbackInvoked.SetResult();
                throw new InvalidOperationException("callback failed");
            });
            ready.SetResult();
            return await outcome;
        };
        await fixture.Land();
        await fixture.Service();
        try
        {
            await TestHangGuard.WaitAsync(ready.Task, "throwing callback registered");
            Assert.Equal(Assert.Single(fixture.Calls).Id, fixture.Coordinator.CancelRunning());
            await TestHangGuard.WaitAsync(callbackInvoked.Task, "throwing callback invoked");
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "callback failure cancellation settled");
            Assert.True(Assert.Single(fixture.Tokens).IsCancellationRequested);
            Assert.True((await fixture.Store.ReadAsync())!.Request!.RetryPending);
        }
        finally
        {
            release.TrySetResult(SelectedExecutionBackstopOutcome.Green);
            await TestHangGuard.WaitAsync(fixture.Coordinator.DrainAsync(), "callback failure cleanup settled");
        }
    }
}
