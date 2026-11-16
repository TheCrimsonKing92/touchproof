using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories and synchronous, thread-local launch observations.
public sealed class WorktreeDirtyTreeEquivalenceTests
{
    [Theory]
    [InlineData("modified")]
    [InlineData("added-untracked")]
    [InlineData("deleted")]
    [InlineData("renamed-staged")]
    [InlineData("renamed-unstaged")]
    [InlineData("executable-bit-head")]
    [InlineData("executable-bit-staged")]
    [InlineData("nested-untracked")]
    [InlineData("ignored-alongside-dirty")]
    [InlineData("directory-emptied")]
    [InlineData("linked-worktree-dirty")]
    [InlineData("canonical-order")]
    [InlineData("canonical-order-head")]
    [InlineData("sha256")]
    [InlineData("unused-filter-driver")]
    public void TryCompute_ModelledDirtyShape_EqualsOriginalFourStepDigest(string shape)
    {
        using var fixture = new WorktreeTreeDigestFixture(
            shape == "unused-filter-driver" ? "[filter \"unused\"]\nclean = cat\nsmudge = cat\n" : null,
            objectFormat: shape == "sha256" ? "sha256" : "sha1");
        var tracked = Path.Combine(fixture.Repo, "tracked.txt");
        switch (shape)
        {
            case "added-untracked": File.WriteAllText(Path.Combine(fixture.Repo, "added"), "new\n"); break;
            case "deleted": File.Delete(tracked); break;
            case "renamed-staged": fixture.Git("mv", "tracked.txt", "renamed.txt"); break;
            case "renamed-unstaged": File.Move(tracked, Path.Combine(fixture.Repo, "renamed.txt")); break;
            case "executable-bit-head":
                fixture.Git("update-index", "--chmod=+x", "tracked.txt");
                fixture.Git("commit", "-m", "executable HEAD");
                Assert.StartsWith("100755 ", fixture.Git("ls-tree", "HEAD", "tracked.txt"));
                File.WriteAllText(tracked, "modified\n");
                break;
            case "executable-bit-staged":
                fixture.Git("update-index", "--chmod=+x", "tracked.txt");
                Assert.StartsWith("100755 ", fixture.Git("ls-files", "--stage", "tracked.txt"));
                break;
            case "nested-untracked":
                var nested = Directory.CreateDirectory(Path.Combine(fixture.Repo, "a", "b", "c")).FullName;
                File.WriteAllText(Path.Combine(nested, "new.txt"), "nested\n");
                break;
            case "ignored-alongside-dirty":
                var ignored = Directory.CreateDirectory(Path.Combine(fixture.Repo, "ignored")).FullName;
                File.WriteAllText(Path.Combine(ignored, "excluded"), "ignored\n");
                File.WriteAllText(tracked, "modified\n");
                break;
            case "directory-emptied":
                var directory = Directory.CreateDirectory(Path.Combine(fixture.Repo, "subtree")).FullName;
                File.WriteAllText(Path.Combine(directory, "a"), "first\n");
                File.WriteAllText(Path.Combine(directory, "b"), "second\n");
                fixture.Git("add", "-A");
                fixture.Git("commit", "-m", "subtree");
                File.Delete(Path.Combine(directory, "a"));
                File.Delete(Path.Combine(directory, "b"));
                break;
            case "linked-worktree-dirty":
                var linked = Path.Combine(fixture.Root, "linked");
                fixture.Git("worktree", "add", "-b", "linked-branch", linked);
                fixture.Repo = linked;
                File.WriteAllText(Path.Combine(linked, "tracked.txt"), "linked dirty\n");
                break;
            case "canonical-order":
            case "canonical-order-head":
                foreach (var name in new[] { "a.", "a0", "é", "space name", "tab\tname" })
                {
                    // Windows forbids trailing dots and control characters in filenames.
                    if (OperatingSystem.IsWindows() && (name.EndsWith('.') || name.Contains('\t'))) continue;
                    File.WriteAllText(Path.Combine(fixture.Repo, name), name + "\n");
                }
                Directory.CreateDirectory(Path.Combine(fixture.Repo, "a"));
                File.WriteAllText(Path.Combine(fixture.Repo, "a", "child"), "child\n");
                if (shape == "canonical-order-head")
                {
                    fixture.Git("add", "-A");
                    fixture.Git("commit", "-m", "non-ASCII HEAD paths");
                    File.WriteAllText(tracked, "modified\n");
                }
                break;
            default: File.WriteAllText(tracked, "modified\n"); break;
        }
        Assert.NotEmpty(fixture.Git("status", "--porcelain=v2", "-z", "--untracked-files=all"));
        Assert.True(GitRefFileResolver.TryResolve(fixture.Repo, "HEAD^{tree}", out var head, fixture.ReadEnvironment));
        Assert.True(GitRevisionReader.TryLocateRefStore(fixture.Repo, fixture.ReadEnvironment, out var git, out _));
        var index = Path.Combine(git, "index");
        var before = File.ReadAllBytes(index);
        var expected = fixture.OriginalDigest();
        var outputCodePages = new List<int?>();
        Assert.Equal(expected, fixture.Compute(launchObserver: start =>
            outputCodePages.Add(start.StandardOutputEncoding?.CodePage)));
        if (shape is "canonical-order" or "canonical-order-head")
            Assert.All(outputCodePages, codePage => Assert.Equal(System.Text.Encoding.UTF8.CodePage, codePage));
        Assert.Equal(before, File.ReadAllBytes(index));
        if (shape != "executable-bit-staged") Assert.NotEqual("wbc1:git-tree:" + head, expected);
        Assert.Equal("status", fixture.Launches[0].Arguments[0]);
        if (shape == "renamed-staged")
        {
            // Status alone cannot prove that the index-only destination is not ignored.
            Assert.Equal(["read-tree", "add", "write-tree"],
                fixture.Launches.TakeLast(3).Select(launch => launch.Arguments[0]).ToArray());
            Assert.InRange(fixture.Launches.Count, 4, 5);
        }
        else
        {
            Assert.InRange(fixture.Launches.Count, 1, 2);
            Assert.All(fixture.Launches.Skip(1), launch => Assert.Equal("ls-tree", launch.Arguments[0]));
        }
    }
}
