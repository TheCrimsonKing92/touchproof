using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a unique repository and changes no process-wide settings.
public sealed class OwnerDigestRevertReaderTests : HostCapacityBoundTestBase
{
    [Fact(DisplayName = "A workspace without its own git entry keeps revert data unavailable")]
    public void Read_NestedNonRepository_ReturnsUnavailable()
    {
        using var repo = new CommitFactMemoRepository();
        var directory = Path.Combine(repo.Root, "workspace");
        Directory.CreateDirectory(directory);

        var data = OwnerDigestRevertReader.Read(directory, "main", null, null, []);

        Assert.Null(data);
        Assert.Equal("not tracked", BuildUnavailableDigest(data).Reverts);
    }

    [Fact(DisplayName = "An unavailable integration branch preserves the not tracked digest value")]
    public void Read_MissingBranch_ReturnsUnavailable()
    {
        using var repo = new CommitFactMemoRepository();

        var data = OwnerDigestRevertReader.Read(repo.Root, "missing-branch", null, null, []);

        Assert.Null(data);
        Assert.Equal("not tracked", BuildUnavailableDigest(data).Reverts);
    }

    [Fact(DisplayName = "Readable git with no revert commits supplies available empty data")]
    public void Read_NoReverts_ReturnsEmptyData()
    {
        using var repo = new CommitFactMemoRepository();

        var data = OwnerDigestRevertReader.Read(repo.Root, "main", null, null,
            [new("a", DateTimeOffset.UnixEpoch, repo.Base, [])]);

        Assert.NotNull(data);
        Assert.Empty(data.Reverts);
        Assert.Empty(data.Landings);
    }

    private static OwnerDigestResult BuildUnavailableDigest(OwnerDigestRevertData? data) =>
        OwnerDigestReport.Build([], [], new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)),
            revertAttribution: data is null ? null : OwnerDigestRevertAttribution.Attribute(data.Reverts, data.Landings));

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
