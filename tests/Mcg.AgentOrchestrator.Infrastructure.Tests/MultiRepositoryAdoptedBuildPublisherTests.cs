using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: hermetic git probes, private registry/database/checkouts and local bare remotes.
public sealed class MultiRepositoryAdoptedBuildPublisherTests
{
    [Fact]
    public void FinalizedAdoption_PushesLandedCommitToResolvedBranch()
    {
        using var h = new Harness();
        var repo = h.Add("companion", "integration");
        var landed = repo.Commit("landed companion");
        repo.Commit("later local work");
        h.Write(("companion", landed));

        h.FinalizeAdoption();

        Assert.Equal(landed, repo.Git("--git-dir=" + repo.Remote, "rev-parse", "refs/heads/integration"));
        Assert.Equal(repo.Baseline, repo.RemoteTip);
        var outcome = Assert.Single(h.Outcomes);
        Assert.Equal("companion", outcome.Repository);
        Assert.Equal(landed, outcome.Commit);
        Assert.True(outcome.Pushed);
        Assert.Equal($"ADOPTED_BUILD_PUSHED commit={landed} remote=origin repository=companion added_lines=1",
            Assert.Single(h.Events.Where(line => line.Contains("repository="))));
        Assert.True(h.HomePushed);
        Assert.Equal(h.HomeCommit, h.Home.RemoteTip);
        Assert.Empty(h.Attention);
    }

    [Fact]
    public void FirstRepositoryDiverges_HoldsOnceAndPublishesNextInDeclaredOrder()
    {
        using var h = new Harness();
        var held = h.Add("z-first");
        held.Git("checkout", "--orphan", "divergent");
        var divergent = held.Commit("unrelated");
        var clean = h.Add("a-second");
        var landed = clean.Commit("clean landed");
        h.Write(("z-first", divergent), ("a-second", landed));

        h.FinalizeAdoption();

        Assert.Equal(new[] { "z-first", "a-second" }, h.Outcomes.Select(outcome => outcome.Repository));
        Assert.False(h.Outcomes[0].Pushed);
        Assert.True(h.Outcomes[1].Pushed);
        Assert.Equal(held.Baseline, held.RemoteTip);
        Assert.Equal(landed, clean.RemoteTip);
        Assert.Equal($"ADOPTED_BUILD_PUSH_HELD commit={divergent} remote=origin repository=z-first reason=non-fast-forward", h.Events[1]);
        Assert.Contains("repository=a-second", h.Events[2]);
        var attention = Assert.Single(h.Attention);
        Assert.Contains("repository=z-first", attention);
        Assert.DoesNotContain("push-adopted", attention);
        Assert.DoesNotContain(held.Calls, args => args.Contains("push"));
        Assert.True(h.HomePushed);
        Assert.Equal(new[] { h.HomeCommit }, h.HomeCalls);
        Assert.Equal($"ADOPTED_BUILD_PUSHED commit={h.HomeCommit} remote=origin added_lines=1", h.Events[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HomePatternMatches_HoldCompanionWithoutPushing(bool ownerMatch)
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        var landed = repo.Commit(ownerMatch ? "fixture-private-value" : AdoptedBuildLeakScannerTests.Token);
        h.Write(("companion", landed));
        Assert.False(File.Exists(Path.Combine(repo.Repository, AdoptedBuildLeakScanner.GenericPatternPath)));

        h.FinalizeAdoption();

        Assert.False(Assert.Single(h.Outcomes).Pushed);
        Assert.Contains("repository=companion reason=scan-match", Assert.Single(h.Events.Where(line => line.Contains("repository="))));
        Assert.Contains("payload.txt:1", Assert.Single(h.Attention));
        Assert.Equal(repo.Baseline, repo.RemoteTip);
        Assert.DoesNotContain(repo.Calls, args => args.Contains("push"));
    }

    [Fact]
    public void MissingHomeOwnerPatterns_HoldsCompanionDespiteItsOwnPatterns()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        h.Write(("companion", repo.Commit("landed")));
        Assert.True(File.Exists(repo.OwnerPath));
        File.Delete(h.Home.OwnerPath);

        h.FinalizeAdoption();

        Assert.False(Assert.Single(h.Outcomes).Pushed);
        Assert.Contains("repository=companion reason=owner-patterns-missing", Assert.Single(h.Events.Where(line => line.Contains("repository="))));
        Assert.Single(h.Attention.Where(body => body.Contains("repository=companion")));
        Assert.Equal(repo.Baseline, repo.RemoteTip);
        Assert.DoesNotContain(repo.Calls, args => args.Contains("push"));
    }

    [Fact]
    public void HomePushHeld_CompanionStillPublishes()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        var landed = repo.Commit("landed");
        h.Write(("companion", landed));
        h.Home.FailPush = true;

        h.FinalizeAdoption();

        Assert.False(h.HomePushed);
        Assert.Contains("reason=push-failed", h.Events[0]);
        Assert.True(Assert.Single(h.Outcomes).Pushed);
        Assert.Equal(landed, repo.RemoteTip);
    }

    [Fact]
    public void MatchingRecords_DeduplicateIdenticalRepositoryAttempts()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        var landed = repo.Commit("landed");
        h.Write(("companion", landed));
        h.Write(("companion", landed));

        h.FinalizeAdoption();

        Assert.True(Assert.Single(h.Outcomes).Pushed);
        Assert.Single(repo.Calls.Where(args => args.Contains("push")));
        Assert.Single(h.Events.Where(line => line.Contains("repository=")));
    }

    [Fact]
    public void MatchingRecords_DifferentSiblingCommitsHoldSecondInGoalOrder()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        var first = repo.Commit("first sibling");
        repo.Git("checkout", "-b", "sibling", repo.Baseline);
        var second = repo.Commit("second sibling");
        h.Write(new GoalId(new string('2', 32)), h.HomeCommit, ("companion", second));
        h.Write(new GoalId(new string('1', 32)), h.HomeCommit, ("companion", first));

        h.FinalizeAdoption();

        Assert.Equal(new[] { first, second }, h.Outcomes.Select(outcome => outcome.Commit));
        Assert.True(h.Outcomes[0].Pushed);
        Assert.False(h.Outcomes[1].Pushed);
        Assert.Equal(first, repo.RemoteTip);
        Assert.Contains("repository=companion reason=non-fast-forward", h.Events[2]);
        Assert.Single(h.Attention);
    }

    [Theory]
    [InlineData("repository-unresolved")]
    [InlineData("landed-commit-missing")]
    public void RepositorySetupHeld_ReportsOnceAndContinues(string reason)
    {
        using var h = new Harness();
        var clean = h.Add("clean");
        var landed = clean.Commit("landed");
        h.Write(("unavailable", reason == "landed-commit-missing" ? "" : landed), ("clean", landed));

        h.FinalizeAdoption();

        Assert.Equal(new[] { false, true }, h.Outcomes.Select(outcome => outcome.Pushed));
        Assert.Contains("repository=unavailable reason=" + reason, h.Events[1]);
        Assert.Single(h.Attention);
        Assert.Equal(landed, clean.RemoteTip);
    }

    [Fact]
    public void UnmatchedAdoption_LeavesCompanionUntouchedAndPreservesHomePush()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        h.Write(GoalId.New(), new string('f', 40), ("companion", repo.Commit("landed")));

        h.FinalizeAdoption();

        Assert.Empty(h.Outcomes);
        Assert.Empty(repo.Calls);
        Assert.Equal(repo.Baseline, repo.RemoteTip);
        Assert.True(h.HomePushed);
        Assert.Equal($"ADOPTED_BUILD_PUSHED commit={h.HomeCommit} remote=origin added_lines=1", Assert.Single(h.Events));
    }

    [Fact]
    public void CancellationDuringRepositoryPush_Propagates()
    {
        using var h = new Harness();
        var repo = h.Add("companion");
        h.Write(("companion", repo.Commit("landed")));
        var publisher = new MultiRepositoryAdoptedBuildPublisher(new(h.Workspace, h.Registry),
            h.GenericPatterns, h.Home.OwnerPath, h.Events.Add, h.Attention.Add,
            (_, _) => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => publisher.Publish(h.Store.FindByLandedHomeCommit(h.HomeCommit)));
        Assert.Empty(h.Events);
        Assert.Empty(h.Attention);
    }

    private sealed class Harness : IDisposable
    {
        internal AdoptedBuildPushFixture Home { get; } = new();
        internal string HomeCommit { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal OrchestratorProjectRegistry Registry { get; }
        internal MultiRepositoryLandingRecordStore Store { get; }
        internal string GenericPatterns { get; }
        internal List<string> Events { get; } = [];
        internal List<string> Attention { get; } = [];
        internal List<string> HomeCalls { get; } = [];
        internal bool HomePushed { get; private set; }
        internal IReadOnlyList<MultiRepositoryAdoptedBuildPublisher.Outcome> Outcomes { get; private set; } = [];
        private readonly List<AdoptedBuildPushFixture> _repositories = [];

        internal Harness()
        {
            HomeCommit = Home.Commit("adopted home");
            Registry = new(Path.Combine(Home.Repository, "registry"), Path.Combine(Home.Repository, "data"));
            Workspace = OrchestratorWorkspace.ForDirectory(Home.Repository, Home.Repository, null, Registry)
                with { IntegrationBranch = "main" };
            Store = new(Workspace.OrchestratorDirectory);
            GenericPatterns = Path.Combine(Home.Repository, AdoptedBuildLeakScanner.GenericPatternPath);
            Directory.CreateDirectory(Path.GetDirectoryName(GenericPatterns)!);
            File.Copy(AdoptedBuildLeakScannerTests.GenericPath, GenericPatterns);
        }

        internal AdoptedBuildPushFixture Add(string key, string branch = "main")
        {
            var repo = new AdoptedBuildPushFixture();
            _repositories.Add(repo);
            if (branch != "main")
            {
                repo.Git("branch", branch, repo.Baseline);
                repo.Git("--git-dir=" + repo.Remote, "update-ref", "refs/heads/" + branch, repo.Baseline);
            }
            Registry.CreateProject(key, repo.Repository, branch);
            return repo;
        }

        internal void Write(params (string Key, string Commit)[] repositories) => Write(GoalId.New(), HomeCommit, repositories);

        internal void Write(GoalId id, string home, params (string Key, string Commit)[] repositories)
        {
            var commits = repositories.Where(pair => pair.Commit.Length != 0).ToDictionary(pair => pair.Key, pair => pair.Commit);
            commits["default"] = home;
            Store.Write(new MultiRepositoryLandingRecord(id, "tuple", commits, new Dictionary<string, string>(),
                repositories.Select(pair => pair.Key).Append("default").ToArray(), DateTimeOffset.UnixEpoch));
        }

        internal void FinalizeAdoption()
        {
            var publisher = new MultiRepositoryAdoptedBuildPublisher(new(Workspace, Registry), GenericPatterns,
                Home.OwnerPath, Events.Add, Attention.Add,
                (directory, args) => _repositories.Single(repo => repo.Repository == directory).Run(directory, args));
            var homeService = new AdoptedBuildPushService(Home.Repository, GenericPatterns, Home.OwnerPath,
                Events.Add, Attention.Add, Home.Run);
            var trigger = new AdoptedBuildPushTrigger(
                () => ConductorAutonomyPolicy.Conservative with { AdoptedBuildPushEnabled = true },
                commit => { HomeCalls.Add(commit); HomePushed = homeService.Push(commit); }, Events.Add, Attention.Add,
                commit => Outcomes = publisher.Publish(Store.FindByLandedHomeCommit(commit)));
            trigger.OnFinalized(HomeCommit, "", "");
        }

        public void Dispose()
        {
            foreach (var repository in _repositories) repository.Dispose();
            Home.Dispose();
        }
    }
}
