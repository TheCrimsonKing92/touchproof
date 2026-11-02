using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Retry contract: acquire asynchronously during initialization, never in a git observer.
public sealed class HostCapacitySlotOnFirstGitTests : IDisposable
{
    private const int HangGuardMilliseconds = 60_000;
    private static readonly TimeSpan HangGuard = TimeSpan.FromMilliseconds(HangGuardMilliseconds);
    private readonly string root = Directory.CreateTempSubdirectory("slot-async-init-").FullName;

    [Fact]
    public async Task NoGitFixtureNeverAcquiresOrWritesASidecar()
    {
        var path = Path.Combine(root, "no-git.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget, usesGit: false);
        await fixture.InitializeAsync();
        File.WriteAllText(Path.Combine(root, "work.txt"), "work without git");
        Assert.Equal(1, budget.AvailableSlots);
        Assert.Equal(0, fixture.AcquisitionRequests);
        await fixture.DisposeAsync();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AsyncInitializationHoldsUntilDisposalAndCountsBothLaunches()
    {
        var path = Path.Combine(root, "launches.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget);
        await fixture.InitializeAsync();
        Assert.Equal(0, budget.AvailableSlots);
        Assert.Equal(1, fixture.AcquisitionRequests);
        File.WriteAllText(Path.Combine(root, "work.txt"), "before first launch");
        var first = GitCli.Run(root, "--version");
        Assert.True(first.Succeeded, first.Error);
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
    public async Task ConcurrentInitializationSharesOneAsynchronousWait()
    {
        var budget = new HostCapacityTestBudget(1, _ => null);
        var held = await budget.EnterAsync();
        await using var fixture = new ProbeFixture(budget);
        // Both calls must return pending tasks on this thread without blocking it.
        var first = fixture.InitializeAsync().AsTask();
        var second = fixture.InitializeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, fixture.AcquisitionRequests);
        }
        finally { await held.DisposeAsync(); }
        await Task.WhenAll(first, second).WaitAsync(HangGuard);
        Assert.Equal(0, budget.AvailableSlots);
        await fixture.DisposeAsync();
        Assert.Equal(1, budget.AvailableSlots);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.InitializeAsync());
    }

    [Fact]
    public async Task NoGitFixtureCompletesWhileAllSlotsAreHeldElsewhere()
    {
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var held = await budget.EnterAsync();
        await using var fixture = new ProbeFixture(budget, usesGit: false);
        await fixture.InitializeAsync().AsTask().WaitAsync(HangGuard);
        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.AcquisitionRequests);
        Assert.Equal(0, budget.AvailableSlots);
    }

    [Fact]
    public async Task ResourceHelperWithoutGitCompletesWhileAllSlotsAreHeldElsewhere()
    {
        var path = Path.Combine(root, "helper.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var held = await budget.EnterAsync();
        await using var fixture = new ResourceProbe(budget);
        File.WriteAllText(Path.Combine(root, "input.txt"), "input");
        await fixture.InitializeAsync().AsTask().WaitAsync(HangGuard);
        Assert.Equal("input.txt", Assert.Single(fixture.Snapshot(root)));
        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.AcquisitionRequests);
        Assert.False(File.Exists(path));
    }

    [Fact(Timeout = HangGuardMilliseconds)]
    public async Task FourTimesCapacityOfGitAndNoGitFixturesAllComplete()
    {
        var budget = new HostCapacityTestBudget(2, _ => null);
        var count = 4 * budget.SlotCount;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var launches = 0;
        var noGitCompleted = 0;

        async Task RunGitFixture()
        {
            await using var fixture = new ProbeFixture(budget);
            await fixture.InitializeAsync();
            if (Interlocked.Increment(ref entered) == budget.SlotCount) full.SetResult();
            await release.Task;
            Assert.Equal(1, fixture.AcquisitionRequests);
            var result = GitCli.Run(root, "--version");
            Assert.True(result.Succeeded, result.Error);
            Interlocked.Increment(ref launches);
        }

        async Task RunNoGitFixture()
        {
            await using var fixture = new ProbeFixture(budget, usesGit: false);
            await fixture.InitializeAsync();
            Assert.Equal(0, fixture.AcquisitionRequests);
            Interlocked.Increment(ref noGitCompleted);
        }

        // Interleave both kinds while admitted git fixtures hold all slots. No Task.Run:
        // a synchronous capacity wait would block the test before it can release them.
        var git = new List<Task>();
        var noGit = new List<Task>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                git.Add(RunGitFixture());
                noGit.Add(RunNoGitFixture());
            }
            await full.Task.WaitAsync(HangGuard);
            await Task.WhenAll(noGit).WaitAsync(HangGuard);
            Assert.Equal(count, noGitCompleted);
            Assert.Equal(0, budget.AvailableSlots);
            Assert.All(git, pending => Assert.False(pending.IsCompleted));
        }
        finally { release.TrySetResult(); }

        await Task.WhenAll(git.Concat(noGit)).WaitAsync(HangGuard);
        Assert.Equal(count, launches);
        Assert.Equal(budget.SlotCount, budget.AvailableSlots);
    }

    private sealed class ProbeFixture(HostCapacityTestBudget budget, bool usesGit = true) : HostCapacityBoundTestBase
    {
        internal int AcquisitionRequests { get; private set; }
        private protected override bool UsesGit => usesGit;
        private protected override HostCapacityTestBudget CapacityBudget
        {
            get { AcquisitionRequests++; return budget; }
        }
    }

    private sealed class ResourceProbe(HostCapacityTestBudget budget) : CliCommandTestBase
    {
        internal int AcquisitionRequests { get; private set; }
        private protected override bool UsesGit => false;
        private protected override HostCapacityTestBudget CapacityBudget
        {
            get { AcquisitionRequests++; return budget; }
        }
        internal string[] Snapshot(string directory) => SnapshotFiles(directory);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
