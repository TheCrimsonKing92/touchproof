using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: overrides are flow-local, storage roots are private, and shutdown
// uses the assembly's permanent suppression switch without changing it.
public sealed class ScopedBuildEnvironmentSeamTests
{
    public enum Seam { Prepare, StaleProbe, Snapshot, Registrar, Shutdown }

    [Theory]
    [InlineData(Seam.Prepare)]
    [InlineData(Seam.StaleProbe)]
    [InlineData(Seam.Snapshot)]
    [InlineData(Seam.Registrar)]
    [InlineData(Seam.Shutdown)]
    public async Task Override_IndependentConcurrentFlow_UsesDefault(Seam seam)
    {
        using var fixture = new Fixture(seam);
        var installed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedDefault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overriding = Task.Run(async () =>
        {
            using var scope = fixture.Install(1);
            installed.SetResult();
            await AwaitSignal(observedDefault.Task, "independent default observed");
            fixture.Invoke(1); // Positive control: this flow must see its own fake.
        });
        var independent = Task.Run(async () =>
        {
            await AwaitSignal(installed.Task, "override installed");
            try
            {
                fixture.Invoke(null);
            }
            finally
            {
                observedDefault.SetResult();
            }
        });
        await AwaitSignal(Task.WhenAll(overriding, independent), "both flows completed");
    }

    [Theory]
    [InlineData(Seam.Prepare)]
    [InlineData(Seam.StaleProbe)]
    [InlineData(Seam.Snapshot)]
    [InlineData(Seam.Registrar)]
    [InlineData(Seam.Shutdown)]
    public async Task Override_NestedDispose_RestoresPreviousAndDefault(Seam seam)
    {
        using var fixture = new Fixture(seam);
        using (var outer = fixture.Install(1))
        {
            fixture.Invoke(1);
            using (var inner = fixture.Install(2))
            {
                fixture.Invoke(2);
                await Task.Run(() => fixture.Invoke(2)); // Normal context inheritance.
                inner.Dispose();
            }
            fixture.Invoke(1);
            outer.Dispose();
        }
        fixture.Invoke(null);
    }

    [Theory]
    [InlineData(Seam.Prepare)]
    [InlineData(Seam.StaleProbe)]
    [InlineData(Seam.Snapshot)]
    [InlineData(Seam.Registrar)]
    [InlineData(Seam.Shutdown)]
    public void Override_NoScope_UsesDefault(Seam seam)
    {
        using var fixture = new Fixture(seam);
        fixture.Invoke(null);
    }

    [Fact]
    public async Task Shutdown_NoInheritedContext_PreservesAssemblyProtection()
    {
        Task independent;
        using (ExecutionContext.SuppressFlow())
            independent = Task.Run(() =>
            {
                // Fail closed: never invoke shutdown if the protection is missing.
                Assert.True(AppContext.TryGetSwitch(
                    DotnetBuildServerRecovery.SuppressShutdownSwitchName, out var suppressed) && suppressed);
                Assert.NotNull(DotnetBuildServerRecovery.ShutdownBuildServersForTests);
                var before = DotnetBuildServerRecovery.LastBuildServerShutdownOutcome;
                DotnetBuildServerRecovery.ShutdownBuildServersBestEffort();
                Assert.Same(before, DotnetBuildServerRecovery.LastBuildServerShutdownOutcome);
            });
        await AwaitSignal(independent, "shutdown protection observed without inherited context");
    }

    [Fact]
    public void Use_NullDelegate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DotnetBuildEnvironmentManager.UsePrepareArtifactsDirectory(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetBuildEnvironmentManager.UseBeforeStaleLeaseIntegrityProbe(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetBuildEnvironmentManager.UseProcessCommandLineSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetBuildEnvironmentManager.UseOwnedRunRootRegistrarFactory(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetBuildServerRecovery.UseShutdownBuildServers(null!));
    }

    private static async Task AwaitSignal(Task task, string signal)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Did not observe {signal}.", exception);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Seam seam;
        private readonly DotnetBuildStorageRoot root = new(CreateTempDirectory());
        private readonly RecordingRegistrar registrar = new();
        private readonly IOwnedRunRootRegistrar? priorRegistrar;
        private readonly List<int> calls = [];
        private readonly ProcessCommandLineSnapshot fakeSnapshot = new(new Dictionary<int, string>());
        private int attempt;

        public Fixture(Seam seam)
        {
            this.seam = seam;
            priorRegistrar = DotnetBuildEnvironmentManager.SetOwnedRunRootRegistrarForTests(root, registrar);
        }

        public IDisposable Install(int value) => seam switch
        {
            Seam.Prepare => DotnetBuildEnvironmentManager.UsePrepareArtifactsDirectory(_ => calls.Add(value)),
            Seam.StaleProbe => DotnetBuildEnvironmentManager.UseBeforeStaleLeaseIntegrityProbe(_ => calls.Add(value)),
            Seam.Snapshot => DotnetBuildEnvironmentManager.UseProcessCommandLineSnapshot(() =>
            {
                calls.Add(value);
                return fakeSnapshot;
            }),
            Seam.Registrar => DotnetBuildEnvironmentManager.UseOwnedRunRootRegistrarFactory(_ =>
            {
                calls.Add(value);
                return null;
            }),
            Seam.Shutdown => DotnetBuildServerRecovery.UseShutdownBuildServers(() => calls.Add(value)),
            _ => throw new ArgumentOutOfRangeException()
        };

        public void Invoke(int? expected)
        {
            calls.Clear();
            switch (seam)
            {
                case Seam.Prepare:
                {
                    var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(root, 0);
                    if (Directory.Exists(environment.ArtifactsPath))
                        Directory.Delete(environment.ArtifactsPath, recursive: true);
                    Assert.False(Directory.Exists(environment.ArtifactsPath));
                    var acquired = Assert.IsType<DotnetBuildLeaseAcquisition.Acquired>(
                        DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(environment, TimeSpan.Zero));
                    using var lease = acquired.Lease;
                    Assert.True(Directory.Exists(environment.ArtifactsPath));
                    using var marker = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
                        Path.Combine(environment.ArtifactsPath, ".mcg-artifacts-owner.json")));
                    Assert.Equal(environment.SlotOwnerToken, marker.RootElement.GetProperty("ownerToken").GetString());
                    break;
                }
                case Seam.StaleProbe:
                {
                    var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(root, 0);
                    using (DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment)) { }
                    var cachedDll = Path.Combine(environment.ArtifactsPath, "bin", "Sample.dll");
                    Directory.CreateDirectory(Path.GetDirectoryName(cachedDll)!);
                    DotnetBuildEnvironmentManagerTests.WriteValidPeFile(cachedDll);
                    File.WriteAllText(environment.ExecutionLockPath, "999999");
                    using var lease = DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(environment);
                    Assert.True(File.Exists(cachedDll));
                    break;
                }
                case Seam.Snapshot:
                {
                    var snapshot = DotnetBuildEnvironmentManager.CreateSlotCandidateProcessSnapshot();
                    if (expected.HasValue)
                        Assert.Same(fakeSnapshot, snapshot);
                    else
                        Assert.NotSame(fakeSnapshot, snapshot);
                    break;
                }
                case Seam.Registrar:
                {
                    var before = registrar.Paths.Count;
                    var environment = RootedDotnetBuildEnvironmentManager.CreateAttempt(
                        root, null, $"scoped-registrar-{++attempt}");
                    if (!expected.HasValue)
                    {
                        Assert.Equal(before + 1, registrar.Paths.Count);
                        Assert.Equal(environment.RootPath, registrar.Paths.Last());
                    }
                    break;
                }
                case Seam.Shutdown:
                {
                    Assert.True(AppContext.TryGetSwitch(
                        DotnetBuildServerRecovery.SuppressShutdownSwitchName, out var suppressed) && suppressed);
                    Assert.NotNull(DotnetBuildServerRecovery.ShutdownBuildServersForTests);
                    var before = DotnetBuildServerRecovery.LastBuildServerShutdownOutcome;
                    DotnetBuildServerRecovery.ShutdownBuildServersBestEffort();
                    Assert.Same(before, DotnetBuildServerRecovery.LastBuildServerShutdownOutcome);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
            if (expected.HasValue)
            {
                Assert.NotEmpty(calls);
                Assert.All(calls, value => Assert.Equal(expected.Value, value));
            }
            else
                Assert.Empty(calls);
        }

        public void Dispose()
        {
            DotnetBuildEnvironmentManager.SetOwnedRunRootRegistrarForTests(root, priorRegistrar);
            Directory.Delete(root.RootPath, recursive: true);
        }
    }

    private sealed class RecordingRegistrar : IOwnedRunRootRegistrar
    {
        public List<string> Paths { get; } = [];
        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner,
            string? goalId, string? dispatchId, DateTimeOffset now) => Paths.Add(canonicalPath);
        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now) { }
        public void MarkRemoved(string canonicalPath, DateTimeOffset now) { }
        public void MarkCleanupFailure(string canonicalPath, string holderEvidence,
            DateTimeOffset nextAttemptAfter, DateTimeOffset now) { }
        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now) { }
    }
}
