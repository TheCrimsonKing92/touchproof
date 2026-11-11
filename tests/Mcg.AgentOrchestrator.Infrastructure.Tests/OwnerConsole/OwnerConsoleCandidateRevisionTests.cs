using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its ordinary metadata files and counted fallback.
public sealed class OwnerConsoleCandidateRevisionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConclusiveRefIsReadWithoutFallbackAndNeverCachesRevision(bool linked, bool packed)
    {
        using var repo = new Repository(linked);
        var calls = 0;
        string? Fallback(string directory, string reference) { calls++; return "fallback"; }
        repo.Write(new string('a', 40), packed);
        Assert.Equal(new string('a', 40), OwnerConsoleCandidateRevision.Read(repo.Root, repo.Goal, Fallback));
        repo.Write(new string('b', 40), packed);
        Assert.Equal(new string('b', 40), OwnerConsoleCandidateRevision.Read(repo.Root, repo.Goal, Fallback));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("locked")]
    [InlineData("symbolic")]
    [InlineData("config")]
    [InlineData("headerless-packed")]
    public void UncertainRefUsesExactGitFallbackAndMissingResultRemainsRetryable(string shape)
    {
        using var repo = new Repository(false);
        repo.Write(new string('a', 40), false);
        if (shape == "missing") File.Delete(repo.RefPath);
        if (shape == "malformed") File.WriteAllText(repo.RefPath, "invalid\n");
        if (shape == "locked") File.WriteAllText(repo.RefPath + ".lock", "pending");
        if (shape == "symbolic") File.WriteAllText(repo.RefPath, "ref: refs/heads/other\n");
        if (shape == "config") File.AppendAllText(Path.Combine(repo.Common, "config"), "[unknown]\nvalue = true\n");
        if (shape == "headerless-packed")
        {
            File.Delete(repo.RefPath);
            File.WriteAllText(Path.Combine(repo.Common, "packed-refs"),
                new string('a', 40) + " refs/heads/" + GoalWorktrees.BranchName(repo.Goal) + "\n");
        }
        var calls = 0;
        string? Fallback(string directory, string reference)
        {
            Assert.Equal(repo.Root, directory);
            Assert.Equal("refs/heads/" + GoalWorktrees.BranchName(repo.Goal), reference);
            calls++;
            return calls == 1 ? null : "fresh-fallback";
        }
        Assert.Null(OwnerConsoleCandidateRevision.Read(repo.Root, repo.Goal, Fallback));
        Assert.Equal("fresh-fallback", OwnerConsoleCandidateRevision.Read(repo.Root, repo.Goal, Fallback));
        Assert.Equal(2, calls);
    }

    private sealed class Repository : IDisposable
    {
        internal readonly string Root = Directory.CreateTempSubdirectory("owner-candidate-").FullName;
        internal readonly GoalId Goal = new("11111111111111111111111111111111");
        internal string Common { get; }
        internal string RefPath => Path.Combine(Common, "refs", "heads", GoalWorktrees.BranchName(Goal));
        internal Repository(bool linked)
        {
            Common = Path.Combine(Root, linked ? "common" : ".git");
            Directory.CreateDirectory(Path.Combine(Common, "objects"));
            Directory.CreateDirectory(Path.GetDirectoryName(RefPath)!);
            File.WriteAllText(Path.Combine(Common, "config"), "[core]\nrepositoryformatversion = 0\nbare = false\n");
            var git = Common;
            if (linked)
            {
                git = Directory.CreateDirectory(Path.Combine(Common, "worktrees", "linked")).FullName;
                File.WriteAllText(Path.Combine(Root, ".git"), "gitdir: " + git + "\n");
                File.WriteAllText(Path.Combine(git, "commondir"), "../..\n");
            }
            File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/" + GoalWorktrees.BranchName(Goal) + "\n");
        }
        internal void Write(string id, bool packed)
        {
            if (packed) File.WriteAllText(Path.Combine(Common, "packed-refs"),
                "# pack-refs with: peeled fully-peeled sorted\n" + id + " refs/heads/" + GoalWorktrees.BranchName(Goal) + "\n");
            else File.WriteAllText(RefPath, id + "\n");
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
