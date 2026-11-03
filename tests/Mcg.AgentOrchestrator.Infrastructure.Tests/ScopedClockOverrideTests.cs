using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: overrides belong to each async flow; launch records use private temporary workspaces.
public sealed class ScopedClockOverrideTests
{
    private static readonly DateTimeOffset First = new(2001, 1, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Second = First.AddDays(1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentFlows_KeepOverlappingClocksIsolated(bool installCommand)
    {
        var firstInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstFlow = Task.Run(async () =>
        {
            try
            {
                using (UseClock(installCommand, () => First))
                {
                    AssertClock(installCommand, First);
                    firstInstalled.SetResult();
                    await WaitFor(secondInstalled.Task, "second flow installed its clock");
                    AssertClock(installCommand, First);
                    firstChecked.SetResult();
                }
                AssertRealClock(installCommand);
            }
            finally
            {
                firstInstalled.TrySetResult();
                firstChecked.TrySetResult();
            }
        });
        var secondFlow = Task.Run(async () =>
        {
            try
            {
                await WaitFor(firstInstalled.Task, "first flow installed its clock");
                AssertRealClock(installCommand);
                using (UseClock(installCommand, () => Second))
                {
                    AssertClock(installCommand, Second);
                    secondInstalled.SetResult();
                    await WaitFor(firstChecked.Task, "first flow checked its overlapping clock");
                    AssertClock(installCommand, Second);
                }
                AssertRealClock(installCommand);
            }
            finally { secondInstalled.TrySetResult(); }
        });

        await WaitFor(Task.WhenAll(firstFlow, secondFlow), "both independent clock flows finished");
        AssertRealClock(installCommand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DescendantFlow_InheritsActiveClock(bool installCommand)
    {
        using (UseClock(installCommand, () => First))
        {
            await WaitFor(Task.Run(() =>
            {
                AssertClock(installCommand, First);
                using (UseClock(installCommand, () => Second))
                    AssertClock(installCommand, Second);
                AssertClock(installCommand, First);
            }), "descendant flow checked its inherited clock");
            AssertClock(installCommand, First);
        }
        AssertRealClock(installCommand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NestedScopes_RestorePreviousClock_AndDisposeOnlyOnce(bool installCommand)
    {
        using (UseClock(installCommand, () => First))
        {
            AssertClock(installCommand, First);
            using (var inner = UseClock(installCommand, () => Second))
            {
                AssertClock(installCommand, Second);
                inner.Dispose();
                AssertClock(installCommand, First);
                using (UseClock(installCommand, () => Second))
                {
                    inner.Dispose();
                    AssertClock(installCommand, Second);
                }
            }
            AssertClock(installCommand, First);
        }
        AssertRealClock(installCommand);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoOverride_ReadsRealUtcClock(bool installCommand) => AssertRealClock(installCommand);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullClock_ThrowsAndPreservesActiveClock(bool installCommand)
    {
        using (UseClock(installCommand, () => First))
        {
            Assert.Throws<ArgumentNullException>(() => UseClock(installCommand, null!));
            AssertClock(installCommand, First);
        }
        AssertRealClock(installCommand);
    }

    private static IDisposable UseClock(bool installCommand, Func<DateTimeOffset> clock) =>
        installCommand ? ProjectInstallManifestCommand.UseClock(clock) : GoalRefinementWorkCoordinator.UseClock(clock);

    private static void AssertClock(bool installCommand, DateTimeOffset expected)
    {
        if (installCommand)
            Assert.Equal(expected, ProjectInstallManifestCommand.UtcNow());
        else
            Assert.Equal("3h07m", GoalRefinementWorkCoordinator.FormatPendingAge(expected.AddHours(-3).AddMinutes(-7)));
    }

    private static void AssertRealClock(bool installCommand)
    {
        if (installCommand)
        {
            var before = DateTimeOffset.UtcNow;
            var actual = ProjectInstallManifestCommand.UtcNow();
            var after = DateTimeOffset.UtcNow;
            Assert.InRange(actual, before, after);
            Assert.Equal(TimeSpan.Zero, actual.Offset);
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "mcg-scoped-clock-" + Guid.NewGuid().ToString("N"));
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var goalId = GoalId.New();
        var previousLaunch = GoalRefinementWorkCoordinator.LaunchOverride;
        var launches = 0;
        GoalRefinementWorkCoordinator.LaunchOverride = (_, _) =>
        {
            launches++;
            return new(true, 1, "test-launch");
        };
        try
        {
            var before = DateTimeOffset.UtcNow;
            var result = GoalRefinementWorkCoordinator.TryLaunchIfDue(workspace, goalId);
            var after = DateTimeOffset.UtcNow;
            Assert.True(result.Started);
            Assert.Equal(1, launches);
            var attempt = SpecRefinementLaunchAttemptStore.ForWorkspace(workspace).Get(goalId);
            Assert.NotNull(attempt);
            Assert.InRange(attempt.LastLaunchAt, before, after);
            Assert.Equal(TimeSpan.Zero, attempt.LastLaunchAt.Offset);
        }
        finally
        {
            GoalRefinementWorkCoordinator.LaunchOverride = previousLaunch;
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static async Task WaitFor(Task signal, string eventName)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Hang waiting for event: {eventName}.", error);
        }
    }
}
