using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Every budget, repository and sidecar is owned by this test. No process-global environment
// or observer replacement is needed, so this class can run alongside the other slot tests.
public sealed class HostCapacitySlotOnFirstGitTests : HostCapacityBoundTestBase
{
    // Bounds detect missing lifecycle/start signals; elapsed time never decides a verdict.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private readonly string root = Directory.CreateTempSubdirectory("slot-first-git-").FullName;
    private readonly HostCapacityTestBudget runnerBudget = new(1, _ => null);

    private protected override HostCapacityTestBudget CapacityBudget => runnerBudget;

    [Fact]
    public void RunnerOwnedFixtureAcquiresEvenWithoutTelemetry()
    {
        Assert.Same(this, Xunit.TestContext.Current.TestClassInstance);
        Assert.Equal(1, runnerBudget.AvailableSlots);
        var result = GitCli.Run(root, "--version");
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(0, runnerBudget.AvailableSlots);
    }

    [Fact]
    public async Task NoGitFixtureNeverAcquiresOrWritesASidecar()
    {
        var path = Path.Combine(root, "no-git.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget);
        await fixture.InitializeAsync();
        File.WriteAllText(Path.Combine(root, "work.txt"), "work without git");
        Assert.Equal(1, budget.AvailableSlots);
        Assert.Equal(0, fixture.AcquisitionRequests);
        await fixture.DisposeAsync();
        Assert.Equal(1, budget.AvailableSlots);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FirstLaunchAfterWorkHoldsUntilDisposalAndCountsBothLaunches()
    {
        var path = Path.Combine(root, "launches.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget);
        await fixture.InitializeAsync();
        File.WriteAllText(Path.Combine(root, "work.txt"), "before first launch");
        Assert.Equal(0, fixture.AcquisitionRequests);
        Assert.Equal(1, budget.AvailableSlots);

        var first = GitCli.Run(root, "--version");
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(0, budget.AvailableSlots);
        Assert.Equal(1, fixture.AcquisitionRequests);
        var second = InfrastructureTestSupport.RunGitProbe(root, ["--version"]);
        Assert.True(second.Succeeded, second.StandardError);
        Assert.Equal(0, budget.AvailableSlots);
        Assert.Equal(1, fixture.AcquisitionRequests);
        Assert.False(File.Exists(path));

        await fixture.DisposeAsync();
        Assert.Equal(1, budget.AvailableSlots);
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        Assert.Equal(HostCapacitySlotTimingWriter.CurrentTestName(),
            row.RootElement.GetProperty("test_name").GetString());
        Assert.Equal(2, row.RootElement.GetProperty("git_launches").GetInt32());
        var command = Assert.Single(row.RootElement.GetProperty("git_subcommands").EnumerateObject());
        Assert.Equal("--version", command.Name);
        Assert.Equal(2, command.Value.GetInt32());
        await fixture.DisposeAsync();
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public async Task ConcurrentFirstLaunchesShareOneWaitBeforeEitherStart()
    {
        var path = Path.Combine(root, "concurrent.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var held = await budget.EnterAsync();
        await using var fixture = new ProbeFixture(budget);
        await fixture.InitializeAsync();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAllowed = 0;
        var starts = 0;
        var window = HostCapacitySlotGitCensus.Shared.Open(HostCapacitySlotTimingWriter.CurrentTestName())!;

        // Return false at the real process-start seam. This proves ordering without creating
        // children or changing the census rule that a failed start is not a counted launch.
        bool Start(System.Diagnostics.Process process)
        {
            Assert.Equal(1, Volatile.Read(ref releaseAllowed));
            Interlocked.Increment(ref starts);
            return false;
        }

        var first = Task.Run(() => InfrastructureTestSupport.RunGitProbe(
            root, ["--version"], startProcess: Start));
        Task<WorkerDispatchTestsSeededRepositoryFactory.GitProbeResult>? second = null;
        try
        {
            await fixture.AcquisitionRequested.Task.WaitAsync(HangGuard);
            second = Task.Run(() =>
            {
                secondEntered.SetResult();
                return InfrastructureTestSupport.RunGitProbe(root, ["--version"], startProcess: Start);
            });
            await secondEntered.Task.WaitAsync(HangGuard);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, fixture.AcquisitionRequests);
            Assert.Equal(0, Volatile.Read(ref starts));
            Assert.Equal(0, window.Close()!.Launches); // Census is unmoved while capacity is held elsewhere.
        }
        finally
        {
            Volatile.Write(ref releaseAllowed, 1);
            await held.DisposeAsync();
            await first.WaitAsync(HangGuard);
            if (second is not null) await second.WaitAsync(HangGuard);
        }

        Assert.Equal(2, starts);
        Assert.False(first.Result.ProcessStarted);
        Assert.False(second!.Result.ProcessStarted);
        Assert.Equal(1, fixture.AcquisitionRequests);
        Assert.Equal(0, budget.AvailableSlots);
        await fixture.DisposeAsync();
        Assert.Equal(1, budget.AvailableSlots);
    }

    [Fact]
    public async Task NoGitFixtureCompletesWhileAllSlotsAreHeldElsewhere()
    {
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var held = await budget.EnterAsync();
        var fixture = new ProbeFixture(budget);
        var lifecycle = Task.Run(async () =>
        {
            await fixture.InitializeAsync();
            File.WriteAllText(Path.Combine(root, "no-git.txt"), "finished");
            await fixture.DisposeAsync();
        });
        try
        {
            await lifecycle.WaitAsync(HangGuard);
            Assert.Equal("finished", File.ReadAllText(Path.Combine(root, "no-git.txt")));
            Assert.Equal(0, fixture.AcquisitionRequests);
            Assert.Equal(0, budget.AvailableSlots);
        }
        finally
        {
            await held.DisposeAsync();
            await lifecycle.WaitAsync(HangGuard);
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResourceHelperWithoutGitCompletesWhileAllSlotsAreHeldElsewhere()
    {
        var path = Path.Combine(root, "helper.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var held = await budget.EnterAsync();
        var fixture = new ResourceProbe(budget);
        File.WriteAllText(Path.Combine(root, "input.txt"), "input");
        var lifecycle = Task.Run(async () =>
        {
            await fixture.InitializeAsync();
            Assert.Equal("input.txt", Assert.Single(fixture.Snapshot(root)));
            await fixture.DisposeAsync();
        });
        try
        {
            await lifecycle.WaitAsync(HangGuard);
            Assert.Equal(0, fixture.AcquisitionRequests);
            Assert.False(File.Exists(path));
        }
        finally
        {
            await held.DisposeAsync();
            await lifecycle.WaitAsync(HangGuard);
            await fixture.DisposeAsync();
        }
    }

    private sealed class ProbeFixture(HostCapacityTestBudget budget) : HostCapacityBoundTestBase
    {
        private int acquisitionRequests;
        internal int AcquisitionRequests => Volatile.Read(ref acquisitionRequests);
        internal TaskCompletionSource AcquisitionRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private protected override HostCapacityTestBudget CapacityBudget
        {
            get
            {
                Interlocked.Increment(ref acquisitionRequests);
                AcquisitionRequested.TrySetResult();
                return budget;
            }
        }
    }

    private sealed class ResourceProbe(HostCapacityTestBudget budget) : CliCommandTestBase
    {
        internal int AcquisitionRequests { get; private set; }
        private protected override HostCapacityTestBudget CapacityBudget
        {
            get
            {
                AcquisitionRequests++;
                return budget;
            }
        }

        internal string[] Snapshot(string directory) => SnapshotFiles(directory);
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
