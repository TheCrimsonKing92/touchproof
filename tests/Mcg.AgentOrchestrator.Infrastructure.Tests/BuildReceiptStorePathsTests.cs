using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real Git metadata and ledgers are confined to each owned fixture.
public sealed class BuildReceiptStorePathsTests
{
    [Fact]
    public void Record_OrdinaryRepository_PreservesDigestAndSourceInvalidation()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        Assert.DoesNotContain(".orchestrator/", File.ReadAllText(Path.Combine(fixture.Root, ".gitignore")));
        Assert.True(WorktreeTreeDigest.TryCompute(fixture.Root, out var before, out var reason), reason);
        var index = File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index"));
        using var errors = new StringWriter();

        new BuildReceiptLedger(error: errors).Record(fixture.Root,
            new(DateTimeOffset.UtcNow, fixture.GoalId, "gate", before, ["App/App.csproj"], 10, 5, null, "none"));

        var expected = Path.Combine(fixture.Root, ".git", ".orchestrator", "build-receipts.jsonl");
        Assert.Equal(expected, BuildReceiptLedger.ResolveStorePath(fixture.Root));
        Assert.Single(File.ReadAllLines(expected));
        Assert.Equal("", errors.ToString());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".orchestrator")));
        Assert.True(WorktreeTreeDigest.TryCompute(fixture.Root, out var after, out reason), reason);
        Assert.Equal(before, after);
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(fixture.Root, ".git", "index")));
        fixture.Change("Core");
        Assert.True(WorktreeTreeDigest.TryCompute(fixture.Root, out var changed, out reason), reason);
        Assert.NotEqual(before, changed);
    }

    [Fact]
    public void Resolve_LinkedWorktree_SharesCommonMetadataLedger()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var linked = Path.Combine(fixture.Root, ".orchestrator-worktrees", "linked");
        fixture.Git("worktree", "add", "--detach", linked, "HEAD");
        Assert.True(File.Exists(Path.Combine(linked, ".git")));

        Assert.Equal(Path.Combine(fixture.Root, ".git", ".orchestrator", "build-receipts.jsonl"),
            BuildReceiptLedger.ResolveStorePath(linked));
        Assert.Equal(BuildReceiptLedger.ResolveStorePath(fixture.Root), BuildReceiptLedger.ResolveStorePath(linked));
    }

    [Fact]
    public void Resolve_ExternalState_PreservesExplicitCustody()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        var state = Path.Combine(Path.GetDirectoryName(fixture.Root)!, Guid.NewGuid().ToString("N"), "state");

        Assert.Equal(Path.Combine(state, "build-receipts.jsonl"), BuildReceiptStorePaths.Resolve(fixture.Root, state));
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void Resolve_BrokenMetadata_SkipsWriteWithoutPollutingSource()
    {
        using var fixture = new WorkerBuildProjectScopeTests.Fixture();
        Directory.Move(Path.Combine(fixture.Root, ".git", "objects"), Path.Combine(fixture.Root, ".git", "retained-objects"));
        using var errors = new StringWriter();
        var ledger = new BuildReceiptLedger(error: errors);
        var receipt = new BuildReceipt(DateTimeOffset.UtcNow, fixture.GoalId, "gate", "unresolved", [], 0, 0, null, "none");

        ledger.Record(fixture.Root, receipt);
        ledger.Record(fixture.Root, receipt);

        Assert.Single(errors.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".orchestrator")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".git", ".orchestrator")));
    }
}
