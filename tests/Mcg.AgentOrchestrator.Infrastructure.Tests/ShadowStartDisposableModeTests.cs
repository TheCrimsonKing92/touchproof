using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

public sealed class ShadowStartDisposableModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("retain")]
    public void DefaultAndExplicitRetainLeaveCopiesWithoutDeletionAuthority(string? retention)
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        var result = fixture.Run("normal", "retained", extra: retention is null ? [] : ["--scratch-retention", retention]);

        Assert.True(result.Exit == 0, result.Error);
        Assert.Contains("mode=retain", result.Output);
        Assert.True(File.Exists(Path.Combine(fixture.Scratch("retained"), ".orchestrator", "state.db")));
        Assert.Empty(Directory.EnumerateFiles(fixture.Registry, "*.run.json"));
    }

    [Theory]
    [InlineData("retain", "--scratch-retention=disposable")]
    [InlineData("disposable", "--scratch-retention=disposable")]
    [InlineData("unknown", null)]
    public void ContradictoryDuplicateOrInvalidModeFailsBeforeScratch(string retention, string? duplicate)
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        var args = new List<string> { "--scratch-retention", retention };
        if (duplicate is not null) args.Add(duplicate);

        var result = fixture.Run("normal", "scratch", extra: args.ToArray());

        Assert.Equal(1, result.Exit);
        Assert.Contains("assertion=arguments", result.Error);
        Assert.False(Directory.Exists(fixture.Scratch("scratch")));
        Assert.False(Directory.Exists(fixture.Registry));
    }

    [Fact]
    public void DisposablePersistsOwnershipBeforeCopyAndStartsOnlyNoDispatchCandidate()
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        var registered = Guid.Empty;
        var hooks = fixture.Hooks with
        {
            BeforeCopy = store =>
            {
                var path = Assert.Single(Directory.EnumerateFiles(fixture.Registry, "*.run.json"));
                var record = JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(path));
                Assert.NotNull(record);
                Assert.Equal(ShadowScratchRetention.Disposable, record.Intent);
                Assert.Equal(ShadowStartPhase.Copying, record.Phase);
                Assert.Equal("state.db", Assert.Single(record.Databases));
                Assert.Equal(Path.Combine(record.State, "state.db"), store.Destination);
                Assert.False(File.Exists(store.Destination));
                Assert.False(File.Exists(store.Staged));
                registered = record.Id;
            }
        };
        var marker = fixture.Scratch("started");
        var result = fixture.Run("normal", "disposable", extra: ["--scratch-retention=disposable"], hooks: hooks, marker: marker);

        Assert.True(result.Exit == (OperatingSystem.IsWindows() ? 0 : 2), result.Error + result.Output);
        Assert.NotEqual(Guid.Empty, registered);
        Assert.Contains($"mode=disposable run_id={registered:N}", result.Output);
        Assert.Equal("started", File.ReadAllText(marker));
        var launch = File.ReadAllLines(Path.Combine(fixture.Scratch("disposable"), "received.txt"));
        Assert.Contains("--no-dispatch", launch);
        Assert.Contains("--disposable-fixture", launch);
    }
}
