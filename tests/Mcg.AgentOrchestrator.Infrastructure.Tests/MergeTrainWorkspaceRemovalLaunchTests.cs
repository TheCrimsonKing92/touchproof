using Mcg.AgentOrchestrator.Infrastructure;
using static GitWorkspaceLaunchFixture;

// Repositories are private to each case; observer windows are synchronous and test-scoped.
public sealed class MergeTrainWorkspaceRemovalLaunchTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void Dispose_Success_RemovesWorkspaceAndEntryWithOneLaunch()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            using var labeler = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(new Labeler());
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "first.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "second.txt", "second");
            MergeTrainWorkspace workspace;
            int created;
            using (var creation = new Recording())
            {
                workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main,
                    [TrainBind(first.GoalId, first.Revision, "first.txt", "first"),
                     TrainBind(second.GoalId, second.Revision, "second.txt", "second")], "main");
                created = creation.Close(null);
            }
            using (workspace)
            {
                Assert.Equal(2, workspace.Members.Count);
                Assert.Empty(workspace.Ejections);
                var admin = AdminDirectory(workspace.Path);
                Assert.True(Directory.Exists(admin));
                AssertEntry(repo, workspace.Path, present: true);
                using var disposal = new Recording();
                workspace.Dispose();
                var disposed = disposal.Close("worktree");
                Assert.Equal(["worktree", "remove", "--force", workspace.Path], Assert.Single(disposal.Arguments));
                Assert.False(Directory.Exists(workspace.Path));
                Assert.False(Directory.Exists(admin));
                AssertEntry(repo, workspace.Path, present: false);
                TestContext.Current.TestOutputHelper!.WriteLine(
                    $"Create-and-dispose git launches={created + disposed}; create={created}; dispose={disposed}; prune=0.");
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remove_HandDeletedTrainDirectory_PrunesOnlyAfterFailedRemove(bool missingBacklink)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            using var labeler = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(new Labeler());
            var main = RunGitOutput(repo, "rev-parse", "main").Trim();
            var first = CreateCandidate(repo, "11111111111111111111111111111111", "first.txt", "first");
            var second = CreateCandidate(repo, "22222222222222222222222222222222", "second.txt", "second");
            var workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main,
                [TrainBind(first.GoalId, first.Revision, "first.txt", "first"),
                 TrainBind(second.GoalId, second.Revision, "second.txt", "second")], "main");
            var admin = AdminDirectory(workspace.Path);
            Directory.Delete(workspace.Path, recursive: true);
            Assert.True(Directory.Exists(admin));
            AssertEntry(repo, workspace.Path, present: true);
            // Git can successfully remove an already absent checkout. A missing backlink
            // makes remove fail to find it, leaving a stale admin entry for prune to clean.
            if (missingBacklink) File.Delete(Path.Combine(admin, "gitdir"));
            using var removal = new Recording();
            GoalWorktrees.RemoveMergeTrainWorkspace(repo, workspace.Path);
            removal.Close(missingBacklink ? ["worktree", "worktree"] : ["worktree"]);
            Assert.Equal(["worktree", "remove", "--force", workspace.Path], removal.Arguments[0]);
            if (missingBacklink) Assert.Equal(["worktree", "prune"], removal.Arguments[1]);
            Assert.False(Directory.Exists(admin));
            AssertEntry(repo, workspace.Path, present: false);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CohortRemove_PrunesOnlyAfterFailedRemove(bool deleted, bool missingBacklink)
    {
        using var fixture = new GitWorkspaceLaunchFixture();
        var repo = fixture.CreateRepository();
        var path = fixture.AddLinkedWorktree(repo);
        var admin = AdminDirectory(path);
        if (deleted) Directory.Delete(path, recursive: true);
        Assert.True(Directory.Exists(admin));
        AssertEntry(repo, path, present: true);
        if (missingBacklink) File.Delete(Path.Combine(admin, "gitdir"));
        using var removal = new Recording();
        GoalWorktrees.RemoveAcceptanceCohortWorkspace(repo, path);
        removal.Close(missingBacklink ? ["worktree", "worktree"] : ["worktree"]);
        Assert.Equal(["worktree", "remove", "--force", path], removal.Arguments[0]);
        if (missingBacklink) Assert.Equal(["worktree", "prune"], removal.Arguments[1]);
        Assert.False(Directory.Exists(path));
        Assert.False(Directory.Exists(admin));
        AssertEntry(repo, path, present: false);
    }

    private static void AssertEntry(string repo, string path, bool present)
    {
        var entries = Git(repo, "worktree", "list", "--porcelain").ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => Normalize(line[9..])).ToArray();
        Assert.NotEmpty(entries);
        Assert.Equal(present, entries.Contains(Normalize(path)));
    }
}
