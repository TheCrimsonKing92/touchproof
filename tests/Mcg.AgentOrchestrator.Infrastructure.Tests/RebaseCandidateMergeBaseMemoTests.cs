using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real merge-base reads in unique repositories, rebase effects captured at its seam.
public sealed class RebaseCandidateMergeBaseMemoTests : HostCapacityBoundTestBase
{
    [Fact]
    public void SameFullIds_LaunchMergeBaseOnce_ChangedBaseLaunchesAgain()
    {
        using var repo = new CommitFactMemoRepository();
        var middle = repo.Commit("middle.txt", "middle");
        var tip = repo.Commit("tip.txt", "tip");
        var rebases = new List<string[]>();
        var first = CommitFactMemoRepository.Capture(() =>
        {
            Step(repo, repo.Base, tip, rebases);
            Step(repo, repo.Base, tip, rebases);
        });
        Assert.Single(first.Where(IsMergeBase));
        Assert.Equal(new[] { repo.Base, repo.Base }, rebases.Select(args => args[^2]));
        var changed = CommitFactMemoRepository.Capture(() => Step(repo, middle, tip, rebases));
        Assert.Single(changed.Where(IsMergeBase));
        Assert.Equal(middle, rebases[^1][^2]);
        Assert.Equal(3, rebases.Count); // Memoization must never skip the rebase itself.
    }

    [Theory]
    [InlineData("HEAD", false)]
    [InlineData("moving", false)]
    [InlineData("HEAD", true)]
    [InlineData("moving", true)]
    public void MovableRevision_InEitherPosition_FollowsBranchMove(string revision, bool inBase)
    {
        using var repo = new CommitFactMemoRepository();
        var middle = repo.Commit("middle.txt", "middle");
        var tip = repo.Commit("tip.txt", "tip");
        repo.Git("checkout", "-b", "moving", repo.Base);
        var rebases = new List<string[]>();
        void Read() => Step(repo, inBase ? revision : middle, inBase ? tip : revision, rebases);
        var first = CommitFactMemoRepository.Capture(Read);
        repo.Git("reset", "--hard", inBase ? middle : tip);
        var second = CommitFactMemoRepository.Capture(Read);
        Assert.Single(first.Where(IsMergeBase));
        Assert.Single(second.Where(IsMergeBase));
        Assert.Equal(new[] { repo.Base, middle }, rebases.Select(args => args[^2]));
    }

    [Fact]
    public void FailedMergeBase_IsNotStored_RetrySucceedsWithLaunch()
    {
        using var local = new CommitFactMemoRepository();
        using var remote = new CommitFactMemoRepository();
        remote.Git("fetch", local.Root, "main");
        remote.Git("reset", "--hard", "FETCH_HEAD");
        var tip = remote.Commit("remote.txt", "remote");
        Step(remote, local.Base, tip, []);
        var rebases = new List<string[]>();
        var failed = StreamComposer.RebaseOntoPriorHead(local.Root, local.Root, local.Base, tip,
            GoalId.New(), "Memo test", _ => throw new InvalidOperationException("No rebase on failure"));
        Assert.Equal(MergeTrainEjectionReason.MaterializationFailure, failed.Ejection?.Reason);
        local.Git("fetch", remote.Root, "main");
        var retry = CommitFactMemoRepository.Capture(() => Step(local, local.Base, tip, rebases));
        Assert.Single(retry.Where(IsMergeBase));
        Assert.Equal(local.Base, Assert.Single(rebases)[^2]);
    }

    private static bool IsMergeBase(string[] args) => args[0] == "merge-base" && args[1] != "--is-ancestor";

    private static void Step(CommitFactMemoRepository repo, string baseId, string tip, List<string[]> rebases)
    {
        var step = StreamComposer.RebaseOntoPriorHead(repo.Root, repo.Root, baseId, tip, GoalId.New(),
            "Memo test", args =>
            {
                Assert.Contains("rebase", args);
                rebases.Add(args);
                return new GitCli.GitResult(0, "", "");
            });
        Assert.Null(step.Ejection);
        Assert.Equal(repo.Git("rev-parse", "HEAD"), step.Head);
    }
}
