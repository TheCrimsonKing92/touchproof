using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsIsolatedSlotGridNegativeControl
    : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Xunit.Fact]
    public async Task ScopeHidesCompetingConsumerAndRestoresItAfterAwait()
    {
        var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0);
        using var sleeper = DotnetBuildEnvironmentManagerTests.StartSleepProcess();
        try
        {
            sleeper.BeginOutputReadLine();
            sleeper.BeginErrorReadLine();
            Assert.False(sleeper.HasExited);
            using var competingSnapshot = DotnetBuildEnvironmentManager.UseProcessCommandLineSnapshot(
                () => new ProcessCommandLineSnapshot(new Dictionary<int, string>
                {
                    [sleeper.Id] = $"testhost.exe --artifacts-path \"{environment.ArtifactsPath}\""
                }));

            AssertCompetingConsumer();
            using (var isolatedGrid = new IsolatedBuildSlotGridScope(StorageRoot))
            {
                await Task.Yield();
                using var lease = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                    DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero)).Lease;
                Assert.Equal(environment.ExecutionLockPath, lease.Environment.ExecutionLockPath);
            }
            AssertCompetingConsumer();

            void AssertCompetingConsumer()
            {
                var result = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero);
                using var unexpectedLease = (result as DotnetBuildLeaseAcquisition.Acquired)?.Lease;
                var busy = Assert.IsType<DotnetBuildLeaseAcquisition.SlotsBusy>(
                    result);
                Assert.Contains(busy.BusySlots, slot => slot.SlotIndex == 0 && slot.OwnerProcessId == sleeper.Id);
            }
        }
        finally
        {
            DotnetBuildEnvironmentManagerTests.StopProcess(sleeper);
        }
    }

    [Xunit.Fact]
    public void InvalidRootsLeaveThePreviousSnapshotInForce()
    {
        var snapshotCalls = 0;
        using var previousSnapshot = DotnetBuildEnvironmentManager.UseProcessCommandLineSnapshot(() =>
        {
            snapshotCalls++;
            return new ProcessCommandLineSnapshot(new Dictionary<int, string>());
        });
        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var invalidRoots = new[]
        {
            tempPath,
            Path.Combine(tempPath + "-sibling", "slots"),
            Path.GetFullPath(Path.Combine(tempPath, "..", "outside-slots"))
        };

        Assert.Throws<ArgumentNullException>(() => new IsolatedBuildSlotGridScope(null!));
        foreach (var path in invalidRoots)
        {
            Assert.Throws<ArgumentException>(() =>
            {
                using var rejected = new IsolatedBuildSlotGridScope(new DotnetBuildStorageRoot(path));
            });
            var before = snapshotCalls;
            using var lease = RootedDotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                StorageRoot, TimeSpan.Zero);
            Assert.True(snapshotCalls > before, "The previous snapshot override must remain active after rejecting a root.");
        }
    }
}
