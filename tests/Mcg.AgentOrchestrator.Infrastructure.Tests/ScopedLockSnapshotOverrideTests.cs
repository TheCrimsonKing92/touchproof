using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: synthetic snapshots and async-flow-local scopes; no other attribution hooks are used.
public sealed class ScopedLockSnapshotOverrideTests
{
    private const string LockedPath = @"C:\repo\locked.dll";

    [Fact]
    public async Task IndependentFlows_KeepOverlappingSnapshotsIsolated()
    {
        var baseline = GetProvider();
        var firstInstalled = NewSignal();
        var secondInstalled = NewSignal();
        var firstChecked = NewSignal();
        var secondChecked = NewSignal();

        var firstFlow = Task.Run(async () =>
        {
            try
            {
                using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(101)))
                {
                    AssertHolder(101);
                    firstInstalled.SetResult();
                    await WaitFor(secondInstalled.Task, "second flow installed its snapshot override");
                    AssertHolder(101);
                    firstChecked.SetResult();
                    await WaitFor(secondChecked.Task, "second flow checked its overlapping snapshot");
                }
                Assert.Equal(baseline, GetProvider());
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
                await WaitFor(firstInstalled.Task, "first flow installed its snapshot override");
                Assert.Equal(baseline, GetProvider());
                using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(202)))
                {
                    AssertHolder(202);
                    secondInstalled.SetResult();
                    await WaitFor(firstChecked.Task, "first flow checked its overlapping snapshot");
                    AssertHolder(202);
                    secondChecked.SetResult();
                }
                Assert.Equal(baseline, GetProvider());
            }
            finally
            {
                secondInstalled.TrySetResult();
                secondChecked.TrySetResult();
            }
        });

        await WaitFor(Task.WhenAll(firstFlow, secondFlow), "both independent snapshot flows finished");
        Assert.Equal(baseline, GetProvider());
    }

    [Fact]
    public void NestedScopes_RestorePredecessor_AndDisposeOnlyOnce()
    {
        AssertDefaultProvider();
        using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(101)))
        {
            AssertHolder(101);
            using (var inner = LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(202)))
            {
                AssertHolder(202);
                inner.Dispose();
                AssertHolder(101);
                using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(303)))
                {
                    inner.Dispose();
                    AssertHolder(303);
                }
            }
            AssertHolder(101);
        }
        AssertDefaultProvider();
    }

    [Fact]
    public void ScopeDisposalAfterException_RestoresDefaultProvider()
    {
        AssertDefaultProvider();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(101));
            AssertHolder(101);
            throw new InvalidOperationException("scope body failed");
        }));
        AssertDefaultProvider();
    }

    [Fact]
    public async Task ChildFlow_InheritsSnapshot_AndRestoresItsOwnReplacement()
    {
        using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(101)))
        {
            await WaitFor(Task.Run(() =>
            {
                AssertHolder(101);
                using (LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(202)))
                    AssertHolder(202);
                AssertHolder(101);
            }), "child flow finished with its inherited snapshot");
            AssertHolder(101);
        }
        AssertDefaultProvider();
    }

    [Fact]
    public void AttributionOperations_AcquireOnceEach_AndKeepTheirOwnSnapshot()
    {
        var calls = 0;
        using var scope = LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(++calls));

        var first = AttributeSnapshot();
        Assert.Equal(1, calls);
        Assert.Equal(1, Assert.Single(first.Holders).ProcessId);

        var second = AttributeSnapshot();
        Assert.Equal(2, calls);
        Assert.Equal(2, Assert.Single(second.Holders).ProcessId);
        Assert.Equal(1, Assert.Single(first.Holders).ProcessId);
        Assert.Equal("process-snapshot", first.Source);
        Assert.Equal("process-snapshot", second.Source);
    }

    [Fact]
    public void NullProvider_IsRejectedWithoutReplacingCurrentScope()
    {
        using var scope = LockAttribution.UseProcessCommandLineSnapshotForTests(() => Snapshot(101));
        Assert.Throws<ArgumentNullException>(() => LockAttribution.UseProcessCommandLineSnapshotForTests(null!));
        AssertHolder(101);
    }

    private static ProcessCommandLineSnapshot Snapshot(int processId) => new(
        new Dictionary<int, ProcessInspectionRecord>
        {
            [processId] = new(
                processId,
                1,
                "dotnet",
                null,
                null,
                $"dotnet test {LockedPath} C:\\repo\\.orchestrator-worktrees\\goal",
                ProcessInspectionStatus.Available)
        });

    // Exercise only the snapshot consumer: public attribution may use unrelated global hooks and OS probes.
    private static BuildLockAttribution AttributeSnapshot()
    {
        var method = typeof(LockAttribution).GetMethod("AttributeFromProcessSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<BuildLockAttribution>(method.Invoke(null, [LockedPath, null]));
    }

    private static void AssertHolder(int processId)
    {
        var attribution = AttributeSnapshot();
        Assert.Equal("process-snapshot", attribution.Source);
        Assert.Equal(processId, Assert.Single(attribution.Holders).ProcessId);
    }

    private static Func<ProcessCommandLineSnapshot> GetProvider()
    {
        var property = typeof(LockAttribution).GetProperty("ProcessCommandLineSnapshotProvider", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(property);
        return Assert.IsType<Func<ProcessCommandLineSnapshot>>(property.GetValue(null));
    }

    private static void AssertDefaultProvider() =>
        Assert.Equal((Func<ProcessCommandLineSnapshot>)ProcessCommandLines.Snapshot, GetProvider());

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitFor(Task task, string expectedEvent)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Hang guard: did not observe {expectedEvent}.");
        }
    }
}
