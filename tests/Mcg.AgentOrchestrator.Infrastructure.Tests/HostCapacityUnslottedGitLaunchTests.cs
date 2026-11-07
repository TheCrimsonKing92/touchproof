using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique destinations and isolated budgets; no observer or environment swaps.
public sealed class HostCapacityUnslottedGitLaunchTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("unslotted-git-").FullName;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EachLaunchWritesOneAttributedUnslottedRow(int launches)
    {
        var path = Path.Combine(root, "launches.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var held = await budget.EnterAsync();
        await using var fixture = new ProbeFixture(budget, path);
        await fixture.InitializeAsync();
        await fixture.InitializeAsync();
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", testName);

        for (var index = 0; index < launches; index++)
            await Task.Run(() => LaunchGit(root));

        await fixture.DisposeAsync();
        Assert.Equal(0, fixture.BudgetReads);
        Assert.Equal(0, budget.AvailableSlots);
        var lines = File.ReadAllLines(path);
        Assert.Equal(launches, lines.Length);
        foreach (var line in lines)
        {
            using var row = JsonDocument.Parse(line);
            AssertUnslottedRow(row.RootElement, testName);
        }
    }

    [Fact]
    public async Task NoLaunchWritesNothing()
    {
        var path = Path.Combine(root, "no-launch.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget, path);
        await fixture.InitializeAsync();
        File.WriteAllText(Path.Combine(root, "work.txt"), "no git");
        await fixture.DisposeAsync();
        Assert.False(File.Exists(path));
        Assert.Equal(0, fixture.BudgetReads);
        Assert.Equal(1, budget.AvailableSlots);
    }

    [Fact]
    public async Task InitializationAndCleanupLaunchesAreRecorded()
    {
        var path = Path.Combine(root, "lifetime.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var fixture = new ProbeFixture(budget, path, lifetimeRoot: root);
        await fixture.InitializeAsync();
        Assert.Single(File.ReadAllLines(path));
        await fixture.DisposeAsync();
        Assert.Equal(2, File.ReadAllLines(path).Length);
        foreach (var line in File.ReadAllLines(path))
        {
            using var row = JsonDocument.Parse(line);
            AssertUnslottedRow(row.RootElement, HostCapacitySlotTimingWriter.CurrentTestName());
        }
        Assert.Equal(0, fixture.BudgetReads);
    }

    [Fact]
    public async Task DisposalStopsRecordingAndCannotReopenTheWatch()
    {
        var path = Path.Combine(root, "closed.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var fixture = new ProbeFixture(budget, path);
        await fixture.InitializeAsync();
        LaunchGit(root);
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
        await fixture.InitializeAsync();
        LaunchGit(root);
        Assert.Single(File.ReadAllLines(path));
        Assert.Equal(0, fixture.BudgetReads);
    }

    [Fact]
    public async Task NestedFixturesDoNotDuplicateLaunchRows()
    {
        var path = Path.Combine(root, "nested.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var outer = new ProbeFixture(budget, path);
        await using var inner = new ProbeFixture(budget, path);
        await outer.InitializeAsync();
        await inner.InitializeAsync();
        LaunchGit(root);
        Assert.Single(File.ReadAllLines(path));
        await inner.DisposeAsync();
        LaunchGit(root);
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task SlottedLaunchStillWritesOnlyTheExistingRow()
    {
        var path = Path.Combine(root, "slotted.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using var fixture = new ProbeFixture(budget, path, usesGit: true);
        await fixture.InitializeAsync();
        LaunchGit(root);
        Assert.False(File.Exists(path));
        await fixture.DisposeAsync();
        using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
        Assert.False(row.RootElement.TryGetProperty("unslotted", out _));
        Assert.Equal(1, row.RootElement.GetProperty("slot_count").GetInt32());
        Assert.Equal(1, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.Equal(1, fixture.BudgetReads);
        Assert.Equal(1, budget.AvailableSlots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledOrUnwritableSidecarDoesNotAffectGit(bool unwritable)
    {
        // An existing directory cannot be opened as a JSONL file.
        var budget = new HostCapacityTestBudget(1, _ => null);
        await using var fixture = new ProbeFixture(budget, unwritable ? root : null);
        await fixture.InitializeAsync();
        LaunchGit(root);
        await fixture.DisposeAsync();
        Assert.Empty(Directory.GetFiles(root));
        Assert.Equal(0, fixture.BudgetReads);
        Assert.Equal(1, budget.AvailableSlots);
    }

    private static void LaunchGit(string directory)
    {
        var result = GitCli.Run(directory, "--version");
        Assert.True(result.Succeeded, result.Error);
    }

    private static void AssertUnslottedRow(JsonElement row, string testName)
    {
        Assert.Equal(testName, row.GetProperty("test_name").GetString());
        Assert.True(row.GetProperty("unslotted").GetBoolean());
        Assert.Equal(0, row.GetProperty("wait_ms").GetDouble());
        Assert.Equal(0, row.GetProperty("held_ms").GetDouble());
        Assert.Equal(0, row.GetProperty("slot_count").GetInt32());
        Assert.Equal(Environment.ProcessId, row.GetProperty("process_id").GetInt32());
        Assert.Equal(1, row.GetProperty("git_launches").GetInt32());
        var command = Assert.Single(row.GetProperty("git_subcommands").EnumerateObject());
        Assert.Equal("--version", command.Name);
        Assert.Equal(1, command.Value.GetInt32());
    }

    private sealed class ProbeFixture(HostCapacityTestBudget budget, string? destination,
        bool usesGit = false, string? lifetimeRoot = null) : HostCapacityBoundTestBase
    {
        private bool cleanedUp;
        internal int BudgetReads { get; private set; }
        private protected override bool UsesGit => usesGit;
        private protected override string? SlotTimingDestination => destination;
        private protected override HostCapacityTestBudget CapacityBudget
        {
            get { BudgetReads++; return budget; }
        }

        public override async ValueTask InitializeAsync()
        {
            await base.InitializeAsync();
            if (lifetimeRoot is not null) LaunchGit(lifetimeRoot);
        }

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (!cleanedUp && lifetimeRoot is not null) LaunchGit(lifetimeRoot);
            }
            finally
            {
                cleanedUp = true;
                await base.DisposeAsync();
            }
        }
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
