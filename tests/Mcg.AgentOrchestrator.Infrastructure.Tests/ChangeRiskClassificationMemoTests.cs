using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each integration case owns its repository and reader handle;
// launch counts use the current test's census window without replacing global observers.
public sealed class ChangeRiskClassificationMemoTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void Classify_UnchangedCommits_LaunchesOneDiff()
    {
        using var fixture = new Fixture();
        fixture.Commit("src/Feature.cs");
        var classify = fixture.NewClassifier();
        var expected = fixture.PreviousResult();
        Assert.Equal(ChangeRiskTier.Behavior, expected);

        AssertDiffLaunches(1, () =>
        {
            Assert.Equal(expected, classify(fixture.Goal));
            Assert.Equal(expected, classify(fixture.Goal));
        });
    }

    [Fact]
    public void Classify_GoalCommitMoves_RecomputesBuildTier()
    {
        using var fixture = new Fixture();
        fixture.Commit("src/Feature.cs");
        var classify = fixture.NewClassifier();
        AssertDiffLaunches(1, () => Assert.Equal(ChangeRiskTier.Behavior, classify(fixture.Goal)));

        fixture.Commit("Directory.Build.props");
        var expected = fixture.PreviousResult();
        Assert.Equal(ChangeRiskTier.Build, expected);
        AssertDiffLaunches(1, () =>
        {
            Assert.Equal(expected, classify(fixture.Goal));
            Assert.Equal(expected, classify(fixture.Goal));
        });
    }

    [Fact]
    public void Classify_IntegrationCommitMoves_RecomputesDocsTier()
    {
        using var fixture = new Fixture();
        var sourceCommit = fixture.Commit("src/Feature.cs");
        fixture.Commit("docs/feature.md");
        var classify = fixture.NewClassifier();
        AssertDiffLaunches(1, () => Assert.Equal(ChangeRiskTier.Behavior, classify(fixture.Goal)));

        RunGit(fixture.Repo, "checkout", fixture.IntegrationBranch);
        RunGit(fixture.Repo, "merge", "--no-ff", sourceCommit, "-m", "integrate source change");
        var expected = fixture.PreviousResult();
        Assert.Equal(ChangeRiskTier.DocsOnly, expected);
        AssertDiffLaunches(1, () =>
        {
            Assert.Equal(expected, classify(fixture.Goal));
            Assert.Equal(expected, classify(fixture.Goal));
        });
    }

    [Fact]
    public void Classify_UnresolvedGoalBranch_RetriesOriginalBranchDiff()
    {
        using var fixture = new Fixture();
        RunGit(fixture.Repo, "checkout", fixture.IntegrationBranch);
        RunGit(fixture.Repo, "branch", "-D", fixture.GoalBranch);
        Assert.False(GitRevisionReader.Shared.TryResolve(fixture.Repo, fixture.GoalBranch, out _));
        var expected = fixture.PreviousResult();
        Assert.Null(expected);
        var classify = fixture.NewClassifier();

        AssertDiffLaunches(2, () =>
        {
            Assert.Equal(expected, classify(fixture.Goal));
            Assert.Equal(expected, classify(fixture.Goal));
        });

        RunGit(fixture.Repo, "checkout", "-b", fixture.GoalBranch);
        fixture.Commit("src/Feature.cs");
        AssertDiffLaunches(1, () => Assert.Equal(ChangeRiskTier.Behavior, classify(fixture.Goal)));
    }

    [Fact]
    public void Classify_EmptyDiff_ReturnsNullAndRetries()
    {
        using var fixture = new Fixture();
        var classify = fixture.NewClassifier();
        Assert.Null(fixture.PreviousResult());

        AssertDiffLaunches(2, () =>
        {
            Assert.Null(classify(fixture.Goal));
            Assert.Null(classify(fixture.Goal));
        });

        fixture.Commit("docs/feature.md");
        AssertDiffLaunches(1, () => Assert.Equal(ChangeRiskTier.DocsOnly, classify(fixture.Goal)));
    }

    [Fact]
    public void Classify_FailedDiffWithResolvedRefs_ReturnsNullAndRetries()
    {
        using var fixture = new Fixture();
        RunGit(fixture.Repo, "checkout", "--orphan", "unrelated");
        fixture.Commit("docs/unrelated.md");
        RunGit(fixture.Repo, "branch", "-f", fixture.GoalBranch, "HEAD");
        Assert.True(GitRevisionReader.Shared.TryResolve(fixture.Repo, fixture.IntegrationBranch, out _));
        Assert.True(GitRevisionReader.Shared.TryResolve(fixture.Repo, fixture.GoalBranch, out _));
        var failed = GitCli.Run(fixture.Repo, "diff", "--name-only",
            $"{fixture.IntegrationBranch}...{fixture.GoalBranch}");
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Null(fixture.PreviousResult());
        var classify = fixture.NewClassifier();

        AssertDiffLaunches(2, () =>
        {
            Assert.Null(classify(fixture.Goal));
            Assert.Null(classify(fixture.Goal));
        });
    }

    [Fact]
    public void Classify_TwoDrivers_DoNotShareResults()
    {
        using var fixture = new Fixture();
        fixture.Commit("src/Feature.cs");
        var first = fixture.NewClassifier();
        var second = fixture.NewClassifier();

        AssertDiffLaunches(2, () =>
        {
            Assert.Equal(ChangeRiskTier.Behavior, first(fixture.Goal));
            Assert.Equal(ChangeRiskTier.Behavior, second(fixture.Goal));
            Assert.Equal(ChangeRiskTier.Behavior, first(fixture.Goal));
            Assert.Equal(ChangeRiskTier.Behavior, second(fixture.Goal));
        });
    }

    [Fact]
    public void GetOrAdd_257thKey_EvictsOldestDespiteRecentHit()
    {
        var memo = new ChangeRiskClassificationMemo();
        var computations = 0;
        ChangeRiskTier? Compute() { computations++; return ChangeRiskTier.Behavior; }
        ChangeRiskTier? Read(int key) => memo.GetOrAdd("integration", key.ToString("x40"), Compute);

        for (var key = 0; key < 256; key++) Assert.Equal(ChangeRiskTier.Behavior, Read(key));
        Assert.Equal(256, memo.Count);
        Assert.Equal(ChangeRiskTier.Behavior, Read(0));
        Assert.Equal(256, computations);
        Assert.Null(memo.GetOrAdd("integration", "failed", () => null));
        Assert.Equal(ChangeRiskTier.Behavior, Read(256));
        Assert.Equal(256, memo.Count);
        Assert.Equal(257, computations);
        Assert.Equal(ChangeRiskTier.Behavior, Read(1));
        Assert.Equal(257, computations);
        Assert.Equal(ChangeRiskTier.Behavior, Read(0));
        Assert.Equal(258, computations);
        Assert.Equal(256, memo.Count);
    }

    private static void AssertDiffLaunches(int expected, Action action)
    {
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(ChangeRiskClassificationMemoTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(
            HostCapacitySlotGitCensus.Shared.Open(identity));
        HostCapacitySlotGitCensus.GitLaunchDelta? delta;
        try { action(); }
        finally { delta = window.Close(); }
        Assert.NotNull(delta);
        Assert.Equal(expected, delta.Subcommands.GetValueOrDefault("diff"));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Repo { get; } = CreateReducedAcceptanceCohortRepository();
        private readonly AgentOrchestratorKernel _kernel = new();
        internal Goal Goal { get; }
        internal string IntegrationBranch { get; }
        internal string GoalBranch => GoalWorktrees.BranchName(Goal.Id);

        internal Fixture()
        {
            try
            {
                IntegrationBranch = OrchestratorWorkspace.ForDirectory(Repo).IntegrationBranch;
                Goal = CreateCompletedGoal(_kernel, "Classify candidate change risk", Repo);
                RunGit(Repo, "checkout", "-b", GoalBranch, IntegrationBranch);
            }
            catch { Dispose(); throw; }
        }

        internal Func<Goal, ChangeRiskTier?> NewClassifier()
        {
            var driver = new ConductorDriver(_kernel, OrchestratorWorkspace.ForDirectory(Repo),
                FakeAcceptanceVerifier.Passed(), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true, cleanupHooks: CreateIsolatedCleanupContext(Repo).Hooks);
            return driver._classifyChangeRisk;
        }

        internal string Commit(string relativePath)
        {
            var path = Path.Combine(Repo, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture change\n");
            RunGit(Repo, "add", "--", relativePath);
            RunGit(Repo, "commit", "-m", $"add {relativePath}");
            return RunGitOutput(Repo, "rev-parse", "HEAD").Trim();
        }

        internal ChangeRiskTier? PreviousResult()
        {
            var result = GitCli.Run(Repo, "diff", "--name-only", $"{IntegrationBranch}...{GoalBranch}");
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output)) return null;
            var files = result.Output.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return (ChangeRiskTier)(int)LandingDecisionEngine.ClassifyRisk(RepositoryChangeClassifier.Classify(files));
        }

        public void Dispose()
        {
            GitRevisionReader.Shared.GetReader(Repo)?.Terminate();
            DeleteDirectory(Repo);
        }
    }
}
