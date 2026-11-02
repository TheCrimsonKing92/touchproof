using System.Reflection;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories, per-test census windows, and owner-only reader cleanup.
public sealed class WorktreeVerifyLookupReaderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("worktree-verify-reader-").FullName;
    private readonly List<string> repositories = [];

    [Fact]
    public void ResolveCommit_ExternalAdvance_PeelsAndReusesReader()
    {
        var repo = Repository();
        Git(repo, "tag", "-a", "annotated", "-m", "annotated");
        var tag = Oracle(repo, "refs/tags/annotated");
        var expected = Oracle(repo, $"{tag}^{{commit}}");
        Assert.NotEqual(tag, expected);
        AssertOneReader(repo, () => Assert.Equal((true, expected), ResolveCommit(repo, tag)));

        var first = Oracle(repo, "refs/heads/main");
        Assert.Equal((true, first), ResolveCommit(repo, first));
        Commit(repo, "external.txt", "external advance\n");
        var second = Oracle(repo, "refs/heads/main");
        Assert.NotEqual(first, second);
        Assert.Equal((true, Oracle(repo, $"{second}^{{commit}}")), ResolveCommit(repo, second));
    }

    [Fact]
    public void ResolveCommit_Unresolvable_PreservesResultAndEmptyOutput()
    {
        var repo = Repository();
        foreach (var sha in new[] { new string('1', 40), Oracle(repo, "HEAD^{tree}") })
        {
            var before = GitCli.Run(repo, "rev-parse", "--verify", "--quiet", $"{sha}^{{commit}}");
            Assert.False(before.Succeeded);
            Assert.Equal((false, string.Empty), ResolveCommit(repo, sha));
            Assert.False(GoalWorktrees.TryComputePatchEquivalence(repo, sha, Oracle(repo, "HEAD"),
                "main", out var evidence, out var refusal));
            Assert.Equal(string.Empty, evidence);
            Assert.Equal("unresolvable-head", refusal);
        }

        // Non-SHAs must still short-circuit before any Git process is launched.
        var delta = Observe(() => Assert.Equal((false, string.Empty), ResolveCommit(repo, "main")));
        Assert.Equal(0, delta.Launches);
    }

    [Fact]
    public void SquashBranch_ExternalRefMove_RecordsOracleHead()
    {
        var f = MergeFixture();
        Git(f.Repo, "checkout", "-b", "candidate", f.Merge);
        var first = Oracle(f.Repo, "refs/heads/candidate");
        var result = SquashBranch(f.Repo, "candidate", f.Main);
        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, result?.Status);
        Assert.Contains($"from {first} (tree {Oracle(f.Repo, $"{first}^{{tree}}")}).",
            Git(f.Repo, "log", "-1", "--format=%B"), StringComparison.Ordinal);

        // A separate git process moves the same branch to a different merge commit.
        var second = Git(f.Repo, "commit-tree", Oracle(f.Repo, $"{f.Merge}^{{tree}}"),
            "-p", f.Merge, "-p", f.Main, "-m", "external merge");
        Git(f.Repo, "update-ref", "refs/heads/candidate", second);
        Assert.NotEqual(first, second);
        Assert.Equal(second, Oracle(f.Repo, "refs/heads/candidate"));
        Assert.Equal(GoalWorktreeRebaseStatus.Rebased, SquashBranch(f.Repo, "candidate", f.Main)?.Status);
        Assert.Contains($"from {second} (tree {Oracle(f.Repo, $"{second}^{{tree}}")}).",
            Git(f.Repo, "log", "-1", "--format=%B"), StringComparison.Ordinal);
    }

    [Fact]
    public void SquashBranch_LinearAndMissing_PreservesNullAndReusesReader()
    {
        var repo = Repository();
        var head = Oracle(repo, "refs/heads/main");
        AssertOneReader(repo, () => Assert.Null(SquashBranch(repo, "main", head)));
        var before = GitCli.Run(repo, "rev-parse", "--verify", "refs/heads/missing");
        Assert.False(before.Succeeded);
        Assert.Null(SquashBranch(repo, "missing", head));
        Assert.Equal(head, Oracle(repo, "HEAD"));
        Assert.Equal(string.Empty, Git(repo, "status", "--porcelain"));
    }

    [Fact]
    public void CurrentMain_ExternalAdvance_UsesUpdatedOracleInEvidence()
    {
        var (repo, oldBase, oldHead) = ConflictFixture();
        var currentMain = Oracle(repo, "refs/heads/main");
        var newHead = RebuildGoal(repo);
        void Check(string main, string candidate)
        {
            Assert.True(GoalWorktrees.TryComputePatchEquivalence(repo, oldHead, candidate,
                "main", out var evidence, out var refusal), refusal);
            Assert.Equal($"goal-owned lines {oldBase}..{oldHead} vs {main}..{candidate}: " +
                "zero-context added and removed lines per file are identical", evidence);
            Assert.Equal(string.Empty, refusal);
        }
        AssertOneReader(repo, () => Check(currentMain, newHead));
        Git(repo, "checkout", "main");
        Commit(repo, "main-latest.txt", "external main advance\n");
        var movedMain = Oracle(repo, "refs/heads/main");
        Assert.NotEqual(currentMain, movedMain);
        var movedCandidate = RebuildGoal(repo);
        Check(movedMain, movedCandidate);

        // The pre-routing operation refuses a missing integration branch at merge-base.
        Assert.False(GitCli.Run(repo, "merge-base", "refs/heads/missing", oldHead).Succeeded);
        Assert.False(GoalWorktrees.TryComputePatchEquivalence(repo, oldHead, movedCandidate,
            "missing", out var missingEvidence, out var missingRefusal));
        Assert.Equal(string.Empty, missingEvidence);
        Assert.Equal("no-merge-base", missingRefusal);
    }

    [Fact]
    public void MergeTree_ExternalRefMove_VerifiesExactTreeAndPreservesRefusals()
    {
        var f = MergeFixture();
        var expectedTree = Oracle(f.Repo, $"{f.Merge}^{{tree}}");
        Assert.Equal(expectedTree, Git(f.Repo, "merge-tree", "--write-tree", f.Main, f.Feature));
        Git(f.Repo, "update-ref", "refs/heads/moving", f.Merge);
        AssertOneReader(f.Repo, () => Assert.Equal((true, string.Empty), VerifyMerges(f.Repo, f.Base, "moving")));

        // A second clean merge has a new tree; the operation receives the externally moved ref.
        Git(f.Repo, "checkout", "feature");
        Commit(f.Repo, "feature-next.txt", "next feature\n");
        var feature = Oracle(f.Repo, "HEAD");
        var tree = Git(f.Repo, "merge-tree", "--write-tree", f.Main, feature);
        var merge = Git(f.Repo, "commit-tree", tree, "-p", f.Main, "-p", feature, "-m", "next merge");
        Git(f.Repo, "update-ref", "refs/heads/moving", merge);
        Assert.NotEqual(expectedTree, Oracle(f.Repo, "moving^{tree}"));
        Assert.Equal(tree, Oracle(f.Repo, $"{merge}^{{tree}}"));
        Assert.Equal((true, string.Empty), VerifyMerges(f.Repo, f.Base, "moving"));

        // Same parents, deliberately different recorded tree: exact pre-routing refusal.
        var bad = Git(f.Repo, "commit-tree", Oracle(f.Repo, $"{f.Main}^{{tree}}"),
            "-p", f.Main, "-p", feature, "-m", "wrong tree");
        Assert.Equal((false, $"integration-merge-not-clean; merge={bad}"), VerifyMerges(f.Repo, f.Base, bad));
        Assert.Equal((false, "integration-merge-unverifiable"), VerifyMerges(f.Repo, f.Base, "missing"));

        // Remove only this fixture's loose root tree to reach the actual tree-lookup miss.
        WithMissingTree(f.Repo, tree, () =>
        {
            Assert.False(GitCli.Run(f.Repo, "rev-parse", "--verify", $"{merge}^{{tree}}").Succeeded);
            Assert.Equal((false, $"integration-merge-unverifiable; merge={merge}"), VerifyMerges(f.Repo, f.Base, merge));
        });
    }

    [Fact]
    public void SquashTree_ExternalAdvance_ReportsOracleTreeAndPreservesMiss()
    {
        var f = MergeFixture();
        var tree = Oracle(f.Repo, $"{f.Merge}^{{tree}}");
        var squash = Git(f.Repo, "commit-tree", tree, "-p", f.Main, "-m", "squash");
        Git(f.Repo, "update-ref", "refs/heads/moving", squash);
        void Check(string oldHead, string newHead, string expectedTree)
        {
            var (success, evidence) = SquashEquivalence(f.Repo, oldHead, newHead);
            Assert.True(success);
            Assert.Equal($"squashed-merge-commits: {newHead} single parent {f.Main} on main; " +
                $"tree {expectedTree} equals git merge-tree --write-tree {f.Main} {oldHead}", evidence);
        }
        AssertOneReader(f.Repo, () => Check(f.Merge, squash, tree));

        Git(f.Repo, "checkout", "feature");
        Commit(f.Repo, "feature-next.txt", "next feature\n");
        var feature = Oracle(f.Repo, "HEAD");
        var movedTree = Git(f.Repo, "merge-tree", "--write-tree", f.Main, feature);
        var movedMerge = Git(f.Repo, "commit-tree", movedTree, "-p", f.Main, "-p", feature, "-m", "next merge");
        var movedSquash = Git(f.Repo, "commit-tree", movedTree, "-p", f.Main, "-m", "next squash");
        Git(f.Repo, "update-ref", "refs/heads/moving", movedSquash);
        var next = Oracle(f.Repo, "refs/heads/moving");
        Assert.NotEqual(squash, next);
        Assert.NotEqual(tree, movedTree);
        Check(movedMerge, next, Oracle(f.Repo, $"{next}^{{tree}}"));

        // A single-parent candidate with a different tree must still refuse equivalence.
        var wrongTree = Oracle(f.Repo, $"{feature}^{{tree}}");
        var wrong = Git(f.Repo, "commit-tree", wrongTree, "-p", f.Main, "-m", "wrong squash");
        Assert.Equal((false, string.Empty), SquashEquivalence(f.Repo, movedMerge, wrong));
        WithMissingTree(f.Repo, wrongTree, () =>
        {
            Assert.False(GitCli.Run(f.Repo, "rev-parse", "--verify", $"{wrong}^{{tree}}").Succeeded);
            Assert.Equal((false, string.Empty), SquashEquivalence(f.Repo, movedMerge, wrong));
        });
    }

    private static (bool Success, string Output) ResolveCommit(string repo, string sha)
    {
        object?[] args = [repo, sha, null];
        var result = Assert.IsType<bool>(Invoke("TryResolveCommit", args));
        return (result, Assert.IsType<string>(args[2]));
    }

    private static (bool Success, string Output) VerifyMerges(string repo, string baseline, string head)
    {
        object?[] args = [repo, baseline, head, null];
        var result = Assert.IsType<bool>(Invoke("TryVerifyIntegrationMergesClean", args));
        return (result, Assert.IsType<string>(args[3]));
    }

    private static (bool Success, string Output) SquashEquivalence(string repo, string oldHead, string newHead)
    {
        object?[] args = [repo, oldHead, newHead, "main", null];
        var result = Assert.IsType<bool>(Invoke("TryComputeSquashEquivalence", args));
        return (result, Assert.IsType<string>(args[4]));
    }

    private static GoalWorktreeRebaseResult? SquashBranch(string repo, string branch, string main) =>
        (GoalWorktreeRebaseResult?)Invoke("TrySquashMergeCommitsOntoBase",
            [repo, repo, branch, "main", new GoalId(Guid.NewGuid().ToString("N")), main]);

    private static object? Invoke(string method, object?[] args)
    {
        var target = typeof(GoalWorktrees).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(target);
        try { return target.Invoke(null, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void AssertOneReader(string repo, Action check)
    {
        Assert.Null(GitRevisionReader.Shared.GetReader(repo));
        var delta = Observe(() => { for (var i = 0; i < 3; i++) check(); });
        Assert.Equal(1, delta.Subcommands.GetValueOrDefault("cat-file"));
        Assert.NotNull(delta.Callers);
        Assert.DoesNotContain(delta.Callers.Keys, key =>
            key.EndsWith("GoalWorktrees.TryReadGit|rev-parse", StringComparison.Ordinal) ||
            key.EndsWith("GitRevisionReader.TryResolve|rev-parse", StringComparison.Ordinal));
    }

    private static HostCapacitySlotGitCensus.GitLaunchDelta Observe(Action action)
    {
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(WorktreeVerifyLookupReaderTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        HostCapacitySlotGitCensus.GitLaunchDelta? delta;
        try { action(); }
        finally { delta = window.Close(); }
        return Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(delta);
    }

    private string Repository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, $"repo-{repositories.Count}")).FullName;
        repositories.Add(repo);
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.name", "Reader");
        Git(repo, "config", "user.email", "reader@example.test");
        Git(repo, "config", "core.autocrlf", "false");
        Git(repo, "config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        Git(repo, "config", "commit.gpgSign", "false");
        Git(repo, "config", "gc.auto", "0");
        Commit(repo, "seed.txt", "seed\n");
        return repo;
    }

    private (string Repo, string Base, string Main, string Feature, string Merge) MergeFixture()
    {
        var repo = Repository();
        var baseline = Oracle(repo, "HEAD");
        Git(repo, "checkout", "-b", "feature");
        Commit(repo, "feature.txt", "feature\n");
        var feature = Oracle(repo, "HEAD");
        Git(repo, "checkout", "main");
        Commit(repo, "main.txt", "main\n");
        var main = Oracle(repo, "HEAD");
        var tree = Git(repo, "merge-tree", "--write-tree", main, feature);
        var merge = Git(repo, "commit-tree", tree, "-p", main, "-p", feature, "-m", "clean merge");
        return (repo, baseline, main, feature, merge);
    }

    private (string Repo, string Base, string Head) ConflictFixture()
    {
        var repo = Repository();
        Commit(repo, "help.txt", "a\nb\nc\nd\n");
        Git(repo, "checkout", "-b", "old");
        Commit(repo, "help.txt", "a\nb\ng1\ng2\nc\nd\n");
        Git(repo, "checkout", "main");
        Commit(repo, "help.txt", "a\nb\nm1\nm2\nc\nd\n");
        var baseline = Oracle(repo, "HEAD");
        Git(repo, "checkout", "old");
        var conflict = GitCli.Run(repo, "merge", "--no-ff", "main", "-m", "conflicting merge");
        Assert.Equal(1, conflict.ExitCode);
        Assert.Contains("CONFLICT", conflict.Output, StringComparison.Ordinal);
        Commit(repo, "help.txt", "a\nb\nm1\nm2\ng1\ng2\nc\nd\n");
        var old = Oracle(repo, "HEAD");
        Git(repo, "checkout", "main");
        Commit(repo, "main-later.txt", "main advance\n");
        Assert.Equal(baseline, Git(repo, "merge-base", "main", old));
        return (repo, baseline, old);
    }

    private string RebuildGoal(string repo)
    {
        Git(repo, "checkout", "--detach", "main");
        Commit(repo, "help.txt", "a\nb\nm1\nm2\ng1\ng2\nc\nd\n");
        return Oracle(repo, "HEAD");
    }

    private void Commit(string repo, string file, string content)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", file);
        Git(repo, "commit", "-m", file);
    }

    private string Git(string repo, params string[] args)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Reader", ["GIT_AUTHOR_EMAIL"] = "reader@example.test",
            ["GIT_COMMITTER_NAME"] = "Reader", ["GIT_COMMITTER_EMAIL"] = "reader@example.test",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
            ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
        };
        var result = GitCli.RunWithEnvironment(repo, environment, args);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Output + result.Error);
        return result.Output.Trim();
    }

    private static string Oracle(string repo, string revision)
    {
        var result = GitCli.Run(repo, "rev-parse", "--verify", revision);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
        return result.Output.Trim();
    }

    private static void WithMissingTree(string repo, string tree, Action check)
    {
        var path = Path.Combine(repo, ".git", "objects", tree[..2], tree[2..]);
        Assert.True(File.Exists(path), "Fixture tree must be a loose object owned by this repository.");
        var hidden = path + ".fixture-missing";
        File.Move(path, hidden);
        try { check(); }
        finally { File.Move(hidden, path); }
    }

    public void Dispose()
    {
        foreach (var repo in repositories)
            GitRevisionReader.Shared.GetReader(repo)?.Terminate();
        SharedTestSupport.RemoveTempDirectory(root);
    }
}
