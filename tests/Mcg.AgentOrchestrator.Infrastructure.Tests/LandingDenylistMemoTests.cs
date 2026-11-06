using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repository keys and thread-local/AsyncLocal observation seams.
public sealed class LandingDenylistMemoTests : HostCapacityBoundTestBase
{
    internal const string FirstJson =
        """{"version":1,"rules":[{"id":"first","reason":"first reason","paths":["first/**"]}]}""";
    private const string SecondJson =
        """{"version":1,"rules":[{"id":"second","reason":"second reason","paths":["second/**"]}]}""";

    [Fact]
    public void SameMainSha_SecondLoadLaunchesNoGit_DifferentShaReadsNewContent()
    {
        using var repo = new CommitFactMemoRepository();
        var firstId = repo.Commit("config/landing-denylist.json", FirstJson);
        LandingExecutor.LoadedLandingDenylist? first = null, second = null, changed = null;
        var initial = CommitFactMemoRepository.Capture(() => first = Load(repo, firstId));
        var repeat = CommitFactMemoRepository.Capture(() => second = Load(repo, firstId));
        Assert.Equal(new[] { "ls-tree", "show" }, initial.Select(args => args[0]));
        Assert.Empty(repeat);
        Assert.Equal(first!.Source, second!.Source);
        Assert.Equal($"main:{firstId}", second.Source);
        AssertRulesEqual(first.Denylist, second.Denylist);
        var next = repo.Commit("config/landing-denylist.json", SecondJson);
        var fresh = CommitFactMemoRepository.Capture(() => changed = Load(repo, next));
        Assert.Equal(new[] { "ls-tree", "show" }, fresh.Select(args => args[0]));
        Assert.Equal("second", Assert.Single(changed!.Denylist.Rules).Id);
        Assert.Equal($"main:{next}", changed.Source);
    }

    [Fact]
    public void CallerMutation_DoesNotChangeStoredRulesOrPaths()
    {
        using var repo = new CommitFactMemoRepository();
        var sha = repo.Commit("config/landing-denylist.json", FirstJson);
        var first = Load(repo, sha);
        var rules = Assert.IsType<LandingDenylist.Rule[]>(first.Denylist.Rules);
        Assert.IsType<string[]>(rules[0].Paths)[0] = "changed/**";
        rules[0] = new("changed", "changed", ["changed/**"]);
        var hits = CommitFactMemoRepository.Capture(() =>
        {
            var second = Load(repo, sha);
            var rule = Assert.Single(second.Denylist.Rules);
            Assert.Equal("first", rule.Id);
            Assert.Equal("first/**", Assert.Single(rule.Paths));
        });
        Assert.Empty(hits);
    }

    [Fact]
    public void TreeWithoutDenylist_ReturnsBuiltInDefault()
    {
        using var repo = new CommitFactMemoRepository();
        var first = Load(repo, repo.Base);
        var launches = CommitFactMemoRepository.Capture(() =>
        {
            var second = Load(repo, repo.Base);
            Assert.Equal(LandingAdmissionReceipt.BuiltInDefaultSource, second.Source);
            AssertRulesEqual(LandingDenylist.BuiltInDefault, second.Denylist);
            AssertRulesEqual(first.Denylist, second.Denylist);
        });
        Assert.Empty(launches);
    }

    [Fact]
    public void UnknownSha_ThrowsAndIsNotStored()
    {
        using var repo = new CommitFactMemoRepository();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var launches = CommitFactMemoRepository.Capture(() =>
            {
                var error = Assert.Throws<InvalidOperationException>(() => Load(repo, new string('1', 40)));
                Assert.StartsWith("Cannot read landing denylist on main:", error.Message);
            });
            Assert.Equal("ls-tree", Assert.Single(launches)[0]);
        }
    }

    [Fact]
    public void FailedRead_IsNotStored_RetrySucceedsWithLaunch()
    {
        using var local = new CommitFactMemoRepository();
        using var remote = new CommitFactMemoRepository();
        var sha = remote.Commit("config/landing-denylist.json", FirstJson);
        _ = Load(remote, sha);
        Assert.Throws<InvalidOperationException>(() => Load(local, sha));
        local.Git("fetch", remote.Root, "main");
        var launches = CommitFactMemoRepository.Capture(() =>
            Assert.Equal("first", Assert.Single(Load(local, sha).Denylist.Rules).Id));
        Assert.Equal(new[] { "ls-tree", "show" }, launches.Select(args => args[0]));
    }

    [Fact]
    public void MalformedConfiguration_IsNotStored()
    {
        using var repo = new CommitFactMemoRepository();
        var sha = repo.Commit("config/landing-denylist.json", "{");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var launches = CommitFactMemoRepository.Capture(() =>
                Assert.StartsWith("invalid:", Load(repo, sha).Source));
            Assert.Equal(new[] { "ls-tree", "show" }, launches.Select(args => args[0]));
        }
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    public void MovableMain_FollowsBranchMove(string revision)
    {
        using var repo = new CommitFactMemoRepository();
        repo.Commit("config/landing-denylist.json", FirstJson);
        Assert.Equal("first", Assert.Single(Load(repo, revision).Denylist.Rules).Id);
        repo.Commit("config/landing-denylist.json", SecondJson);
        var launches = CommitFactMemoRepository.Capture(() =>
            Assert.Equal("second", Assert.Single(Load(repo, revision).Denylist.Rules).Id));
        Assert.Equal(new[] { "ls-tree", "show" }, launches.Select(args => args[0]));
    }

    [Fact]
    public void RunnerOverride_NeitherReadsNorWritesTheMemo()
    {
        using var repo = new CommitFactMemoRepository();
        var sha = repo.Commit("config/landing-denylist.json", FirstJson);
        _ = Load(repo, sha);
        var calls = 0;
        using (LandingExecutor.UseGitRunner((_, args) =>
        {
            calls++;
            return new(0, args[0] == "show" ? SecondJson : "config/landing-denylist.json", "");
        })) Assert.Equal("second", Assert.Single(Load(repo, sha).Denylist.Rules).Id);
        Assert.Equal(2, calls);
        Assert.Equal("first", Assert.Single(Load(repo, sha).Denylist.Rules).Id);
    }

    private static LandingExecutor.LoadedLandingDenylist Load(CommitFactMemoRepository repo, string sha) =>
        LandingExecutor.LoadLandingDenylist(repo.Root, sha);

    private static void AssertRulesEqual(LandingDenylist expected, LandingDenylist actual) =>
        Assert.Equal(expected.Rules.Select(rule => (rule.Id, rule.Reason, string.Join('\0', rule.Paths))),
            actual.Rules.Select(rule => (rule.Id, rule.Reason, string.Join('\0', rule.Paths))));
}
