using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe repositories; the global observer forwards other observers and filters by thread.
public sealed class MergeCommitsOutsideBaseMemoTests : HostCapacityBoundTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SecondCallWithSameFullIds_LaunchesNoGitAndReturnsSameAnswer(bool containsMerge)
    {
        using var repo = new CommitFactMemoRepository();
        var tip = containsMerge ? repo.MergeTip() : repo.Commit("linear.txt", "linear");
        using var observer = new Launches();
        Assert.Equal(containsMerge, GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root, repo.Base, tip));
        Assert.Equal(new[] { "rev-list" }, observer.Commands);
        observer.Commands.Clear();
        Assert.Equal(containsMerge, GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root, repo.Base, tip));
        Assert.Empty(observer.Commands);
    }

    [Fact]
    public void NewMergeCommitTip_LaunchesGitAndReturnsNewAnswer()
    {
        using var repo = new CommitFactMemoRepository();
        var tip = repo.Commit("linear.txt", "linear");
        Assert.False(GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root, repo.Base, tip));
        Assert.Empty(CommitFactMemoRepository.Capture(() =>
            Assert.False(GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root, repo.Base, tip))));
        repo.Git("branch", "side", repo.Base);
        repo.Git("checkout", "side");
        repo.Commit("side.txt", "side");
        repo.Git("checkout", "main");
        repo.Git("merge", "--no-ff", "--no-edit", "side");
        var merged = repo.Git("rev-parse", "HEAD");
        Assert.NotEqual(tip, merged);
        var launches = CommitFactMemoRepository.Capture(() =>
            Assert.True(GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root, repo.Base, merged)));
        Assert.Equal("rev-list", Assert.Single(launches)[0]);
    }

    [Theory]
    [InlineData("HEAD", false)]
    [InlineData("candidate", false)]
    [InlineData("HEAD", true)]
    [InlineData("candidate", true)]
    public void MovableRevision_InEitherPosition_FollowsBranchMove(string revision, bool inBase)
    {
        using var repo = new CommitFactMemoRepository();
        var merged = repo.MergeTip();
        repo.Git("reset", "--hard", repo.Base);
        bool Read() => GoalWorktrees.HasMergeCommitsOutsideBase(repo.Root,
            inBase ? revision : repo.Base, inBase ? merged : revision);
        var first = CommitFactMemoRepository.Capture(() => Assert.Equal(inBase, Read()));
        repo.Git("reset", "--hard", merged);
        var second = CommitFactMemoRepository.Capture(() => Assert.Equal(!inBase, Read()));
        Assert.Equal("rev-list", Assert.Single(first)[0]);
        Assert.Equal("rev-list", Assert.Single(second)[0]);
    }

    [Fact]
    public void FailedRead_IsNotStored_RetrySucceedsWithLaunch()
    {
        using var local = new CommitFactMemoRepository();
        using var remote = new CommitFactMemoRepository();
        remote.Git("fetch", local.Root, "main");
        remote.Git("reset", "--hard", "FETCH_HEAD");
        var tip = remote.MergeTip();
        Assert.True(GoalWorktrees.HasMergeCommitsOutsideBase(remote.Root, remote.Base, tip));
        // A populated key in another repository must not hide locally missing objects.
        Assert.False(GoalWorktrees.HasMergeCommitsOutsideBase(local.Root, remote.Base, tip));
        local.Git("fetch", remote.Root, "candidate");
        var launches = CommitFactMemoRepository.Capture(() =>
            Assert.True(GoalWorktrees.HasMergeCommitsOutsideBase(local.Root, remote.Base, tip)));
        Assert.Equal("rev-list", Assert.Single(launches)[0]);
    }

    private sealed class Launches : IDisposable
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private Action<string>? previous;
        private readonly Action<string> observer;
        private int active = 1;
        internal List<string> Commands { get; } = [];

        internal Launches()
        {
            observer = command =>
            {
                if (Volatile.Read(ref active) != 0 && Environment.CurrentManagedThreadId == thread)
                    Commands.Add(command);
                previous?.Invoke(command);
            };
            do { previous = Volatile.Read(ref GitCli.LaunchObserver); }
            while (Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous) != previous);
        }

        public void Dispose()
        {
            Volatile.Write(ref active, 0);
            Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer);
        }
    }
}
