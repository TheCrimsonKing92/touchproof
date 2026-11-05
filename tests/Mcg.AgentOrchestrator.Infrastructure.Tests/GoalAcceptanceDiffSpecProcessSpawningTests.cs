using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Repository = GoalGitFactIndexProcessSpawningTests.Repository;

// Serial: shares the revision-reader owner; observes launches only on the current thread.
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GoalAcceptanceDiffSpecProcessSpawningTests
{
    [Xunit.Fact]
    public void ChangedFiles_IntegrationPresent_ResolvesThenLaunchesOnlyDiff()
    {
        using var repo = new Repository();
        repo.Run("branch", "main");
        repo.Commit("change");
        Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "main"));
        using var launches = new Launches();

        var files = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main");

        // Cold reader startup is separate from base-discovery probes and the diff.
        Assert.Equal(new[] { "cat-file", "diff" }, launches.Commands);
        Assert.Equal("main...HEAD", Assert.Single(launches.Diffs)[^1]);
        Assert.Equal(new[] { "change.txt" }, files);
        launches.Clear();

        files = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main");

        Assert.Equal(new[] { "diff" }, launches.Commands);
        Assert.Equal("main...HEAD", Assert.Single(launches.Diffs)[^1]);
        Assert.Equal(new[] { "change.txt" }, files);
    }

    [Xunit.Fact]
    public void ChangedFiles_IntegrationAbsent_ConfirmsAbsenceAndResolvesMaster()
    {
        using var repo = new Repository();
        repo.Run("branch", "master");
        repo.Commit("change");
        Assert.True(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "main"));
        Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "master"));
        using var launches = new Launches();

        var files = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main");

        Assert.Equal(new[] { "cat-file", "diff" }, launches.Commands);
        Assert.Equal("master...HEAD", Assert.Single(launches.Diffs)[^1]);
        Assert.Equal(new[] { "change.txt" }, files);
    }

    [Xunit.Fact]
    public void ChangedFiles_BothBranchesAbsent_ProbesParentThroughGit()
    {
        using var repo = new Repository();
        repo.Commit("change");
        Assert.True(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "main"));
        Assert.True(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "master"));
        using var launches = new Launches();

        var files = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main");

        Assert.Equal(new[] { "rev-parse", "diff" }, launches.Commands);
        Assert.Equal(new[] { "rev-parse", "--verify", "--quiet", "HEAD~1" }, Assert.Single(launches.Probes));
        Assert.Equal("HEAD~1...HEAD", Assert.Single(launches.Diffs)[^1]);
        Assert.Equal(new[] { "change.txt" }, files);
    }

    [Xunit.Fact]
    public void ChangedFiles_PackedIntegration_ResolvesWithoutExtraProbe()
    {
        using var repo = new Repository();
        repo.Run("branch", "main");
        repo.Commit("change");
        repo.Run("pack-refs", "--all");
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "refs", "heads", "main")));
        Assert.Contains("refs/heads/main", File.ReadAllText(Path.Combine(repo.Root, ".git", "packed-refs")));
        Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "main"));
        using var launches = new Launches();

        var files = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main");

        Assert.Equal(new[] { "cat-file", "diff" }, launches.Commands);
        Assert.Empty(launches.Probes);
        Assert.Equal("main...HEAD", Assert.Single(launches.Diffs)[^1]);
        Assert.Equal(new[] { "change.txt" }, files);
    }

    [Xunit.Fact]
    public void ChangedFiles_UndecidedCorruptRef_PreservesProbeFailureFields()
    {
        using var repo = new Repository();
        File.WriteAllText(Path.Combine(repo.Root, ".git", "refs", "heads", "main"), "not-an-object-id\n");
        Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo.Root, "main"));
        Assert.False(GitRevisionReader.Shared.TryResolve(repo.Root, "main", out _));
        using var launches = new Launches();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(repo.Root, "main"));

        Assert.Contains(launches.Probes, args => args.SequenceEqual(new[] { "rev-parse", "--verify", "--quiet", "main" }));
        Assert.StartsWith("git base-ref discovery failed: ref=main; process-started=true; exit-code=", exception.Message);
        Assert.Contains("; drain-timed-out=false; detail=", exception.Message);
        Assert.Empty(launches.Diffs);
    }

    private sealed class Launches : IDisposable
    {
        private readonly Action<ProcessStartInfo>? previous = GitCli.StartInfoObserver;
        internal List<string> Commands { get; } = [];
        internal List<string[]> Probes { get; } = [];
        internal List<string[]> Diffs { get; } = [];

        internal Launches() => GitCli.StartInfoObserver = info =>
        {
            previous?.Invoke(info);
            var args = info.ArgumentList.ToArray();
            // GitCli prepends -c settings; the persistent reader also pins --git-dir.
            var offset = 0;
            while (offset < args.Length)
            {
                if (args[offset] == "-c") offset += 2;
                else if (args[offset].StartsWith("--git-dir=", StringComparison.Ordinal)) offset++;
                else break;
            }
            args = args[offset..];
            Commands.Add(args[0]);
            if (args[0] == "rev-parse") Probes.Add(args);
            if (args[0] == "diff") Diffs.Add(args);
        };

        internal void Clear()
        {
            Commands.Clear();
            Probes.Clear();
            Diffs.Clear();
        }

        public void Dispose() => GitCli.StartInfoObserver = previous;
    }
}
