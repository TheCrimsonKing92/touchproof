using System.Globalization;
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

    [Fact]
    public void Read_MergeThenRevert_AttributesOnlyIntroducedCommits()
    {
        using var repo = new CommitFactMemoRepository();
        var mainSide = repo.Commit("main.txt", "main-side change");
        repo.Git("checkout", "-b", "goal", repo.Base);
        var first = repo.Commit("first.txt", "first goal change");
        var second = repo.Commit("second.txt", "second goal change");
        repo.Git("checkout", "main");
        repo.Git("merge", "--no-ff", "--no-edit", "goal");
        var merge = repo.Git("rev-parse", "HEAD");
        Assert.Equal(mainSide, repo.Git("rev-parse", merge + "^1"));
        Assert.Equal(second, repo.Git("rev-parse", merge + "^2"));
        repo.Git("revert", "--no-edit", first);
        var revertSha = repo.Git("rev-parse", "HEAD");
        var revertMessage = repo.Git("show", "-s", "--format=%B", revertSha);
        Assert.Contains($"This reverts commit {first}.", revertMessage);
        Assert.False(File.Exists(Path.Combine(repo.Root, "first.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "second.txt")));
        var revertAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(
            repo.Git("show", "-s", "--format=%ct", revertSha), CultureInfo.InvariantCulture));
        OwnerDigestGoalInput[] goals = [new("goal", revertAt.AddSeconds(-1), merge, [])];

        var data = OwnerDigestRevertReader.Read(repo.Root, "main", null, null, goals);

        Assert.NotNull(data);
        var revert = Assert.Single(data.Reverts);
        Assert.Equal(revertSha, revert.Sha);
        Assert.Equal(revertMessage, revert.Message.Trim());
        var landing = Assert.Single(data.Landings);
        Assert.Equal("goal", landing.GoalId);
        Assert.Equal(new[] { merge, first, second }.Order(StringComparer.Ordinal),
            landing.IntroducedShas.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(repo.Base, landing.IntroducedShas);
        Assert.DoesNotContain(mainSide, landing.IntroducedShas);
        var attribution = OwnerDigestRevertAttribution.Attribute(data.Reverts, data.Landings);
        Assert.Equal(1, attribution.Count);
        Assert.Equal(new OwnerDigestRevertMatch(revertSha, first, "goal"),
            Assert.Single(attribution.Matches));

        // Bounds come from the commit object, so host speed cannot change the verdict.
        var inclusive = OwnerDigestRevertReader.Read(repo.Root, "main", revertAt,
            revertAt.AddSeconds(1), goals);
        Assert.NotNull(inclusive);
        Assert.Equal(revertSha, Assert.Single(inclusive.Reverts).Sha);
        var before = OwnerDigestRevertReader.Read(repo.Root, "main", null, revertAt, goals);
        Assert.NotNull(before);
        Assert.Empty(before.Reverts);
        var after = OwnerDigestRevertReader.Read(repo.Root, "main", revertAt.AddSeconds(1), null, goals);
        Assert.NotNull(after);
        Assert.Empty(after.Reverts);
    }

    private static OwnerDigestResult BuildUnavailableDigest(OwnerDigestRevertData? data) =>
        OwnerDigestReport.Build([], [], new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)),
            revertAttribution: data is null ? null : OwnerDigestRevertAttribution.Attribute(data.Reverts, data.Landings));

    private sealed record FixedClock(DateTimeOffset UtcNow) : IClock;
}
