using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories, isolated child environments, synchronous thread-local observer.
public sealed class WorktreeTreeDigestEquivalenceTests
{
    [Theory]
    [InlineData("clean")]
    [InlineData("modified-tracked")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("ignored-only")]
    [InlineData("removed-tracked")]
    [InlineData("executable-bit")]
    [InlineData("gitattributes-eol")]
    [InlineData("linked-worktree")]
    public void TryCompute_FixtureWorktree_EqualsOriginalFourStepDigest(string shape)
    {
        using var fixture = new WorktreeTreeDigestFixture();
        var tracked = Path.Combine(fixture.Repo, "tracked.txt");
        switch (shape)
        {
            case "modified-tracked": File.WriteAllText(tracked, "modified\n"); break;
            case "staged":
                File.WriteAllText(tracked, "staged\n");
                fixture.Git("add", "tracked.txt");
                Assert.NotEmpty(fixture.Git("diff", "--cached", "--name-only"));
                break;
            case "untracked": File.WriteAllText(Path.Combine(fixture.Repo, "new.txt"), "new\n"); break;
            case "ignored-only":
                Directory.CreateDirectory(Path.Combine(fixture.Repo, "ignored"));
                File.WriteAllText(Path.Combine(fixture.Repo, "ignored", "new.txt"), "ignored\n");
                break;
            case "removed-tracked": File.Delete(tracked); break;
            case "executable-bit":
                fixture.Git("update-index", "--chmod=+x", "tracked.txt");
                Assert.StartsWith("100755 ", fixture.Git("ls-files", "--stage", "tracked.txt"));
                break;
            case "gitattributes-eol":
                // Seed the canonical CRLF checkout with matching index stat data before asserting clean status.
                File.WriteAllText(Path.Combine(fixture.Repo, ".gitattributes"), "*.txt text eol=crlf\n");
                File.WriteAllText(tracked, "one\r\ntwo\r\n");
                fixture.Git("add", "-A");
                fixture.Git("commit", "-m", "seed CRLF checkout");
                Assert.Equal("one\ntwo\n", fixture.Git("show", "HEAD:tracked.txt"));
                Assert.Contains("\r\n", File.ReadAllText(tracked));
                break;
            case "linked-worktree":
                var linked = Path.Combine(fixture.Root, "linked");
                fixture.Git("worktree", "add", "-b", "linked-branch", linked);
                fixture.Repo = linked;
                Assert.True(File.Exists(Path.Combine(linked, ".git")));
                break;
        }
        var clean = shape is "clean" or "ignored-only" or "gitattributes-eol" or "linked-worktree";
        var status = fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all", "--ignore-submodules=none");
        Assert.Equal(clean, status.Length == 0);
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out var headTree, fixture.ReadEnvironment));
        var indexBefore = File.ReadAllBytes(IndexPath(fixture));
        var expected = fixture.OriginalDigest();
        var actual = fixture.Compute();
        Assert.Equal(expected, actual);
        Assert.Equal(indexBefore, File.ReadAllBytes(IndexPath(fixture)));
        if (clean) Assert.Equal("wbc1:git-tree:" + headTree, actual);
        else if (shape != "executable-bit") Assert.NotEqual("wbc1:git-tree:" + headTree, actual);
        Assert.Equal(clean ? 1 : 4, fixture.Launches.Count);
    }

    private static string IndexPath(WorktreeTreeDigestFixture fixture)
    {
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Repo, fixture.ReadEnvironment, out var git, out _));
        return Path.Combine(git, "index");
    }
}
