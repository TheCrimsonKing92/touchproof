using Mcg.AgentOrchestrator.Infrastructure;

// Shared by the four site contract tests. Every instance owns its repository and linked worktree.
internal sealed class InProcessGitFixture : IDisposable
{
    private readonly string root = SharedTestSupport.CreateTempDirectory();
    internal string Repository { get; }
    internal string Worktree { get; }
    internal string Branch => Worktree == Repository ? "trunk" : "topic";

    internal InProcessGitFixture(string layout = "loose", bool commit = true)
    {
        Repository = Path.Combine(root, "repo");
        Directory.CreateDirectory(Repository);
        Worktree = Repository;
        Git("init", "-b", "trunk");
        Git("config", "user.email", "in-process@example.invalid");
        Git("config", "user.name", "In Process Fixture");
        if (!commit) return;
        File.WriteAllText(Path.Combine(Repository, "seed.txt"), "seed\n");
        Git("add", "seed.txt");
        Git("commit", "-m", "seed");
        Git("branch", "topic");
        File.WriteAllText(Path.Combine(Repository, "seed.txt"), "trunk advances\n");
        Git("add", "seed.txt");
        Git("commit", "-m", "advance trunk");
        if (layout is "packed" or "linked")
        {
            Git("pack-refs", "--all");
            Assert.False(File.Exists(Path.Combine(Repository, ".git", "refs", "heads", "trunk")));
            Assert.False(File.Exists(Path.Combine(Repository, ".git", "refs", "heads", "topic")));
            // MetadataLookup deliberately declines a packed-only store. Keep an unrelated loose
            // ref while proving that all queried branch refs still come from packed-refs.
            Git("tag", "loose-marker");
        }
        if (layout == "linked")
        {
            Worktree = Path.Combine(root, "linked");
            Run(Repository, "worktree", "add", Worktree, "topic");
            Assert.True(File.Exists(Path.Combine(Worktree, ".git")));
        }
    }

    internal string Git(params string[] arguments) => Run(Worktree, arguments);

    internal static string Run(string directory, params string[] arguments)
    {
        // Oracles must execute Git even for shapes that GitCli.Run can answer from metadata.
        var result = GitCli.RunDirect(directory, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    internal void AssertRevisionParity()
    {
        foreach (var revision in new[] { "HEAD", "refs/heads/trunk", "trunk^{commit}", "HEAD^{tree}" })
        {
            var expected = Git("rev-parse", "--verify", revision);
            var launches = new LaunchRecording();
            Assert.True(GitRevisionReader.Shared.TryResolve(Worktree, revision, out var actual));
            Assert.Equal(expected, actual);
            launches.AssertRevParseCount(0);
        }
    }

    public void Dispose()
    {
        if (Worktree != Repository) Run(Repository, "worktree", "remove", "--force", Worktree);
        SharedTestSupport.RemoveTempDirectory(root);
    }

    internal sealed class LaunchRecording
    {
        private readonly HostCapacitySlotGitCensus.Window window;

        internal LaunchRecording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(InProcessGitFixture));
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        }

        internal void AssertRevParseCount(int expected, int? expectedBranch = null)
        {
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            Assert.Equal(expected, delta.Subcommands.GetValueOrDefault("rev-parse"));
            if (expectedBranch is { } branchCount)
                Assert.Equal(branchCount, delta.Subcommands.GetValueOrDefault("branch"));
        }
    }
}
