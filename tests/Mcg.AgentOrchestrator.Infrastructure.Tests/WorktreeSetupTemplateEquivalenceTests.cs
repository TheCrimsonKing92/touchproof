using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: instance recipes and independently owned repositories/stores.
public sealed class WorktreeSetupTemplateEquivalenceTests : GoalWorktreeTestBase
{
    [Fact]
    public void ManifestCopies_MatchHandBuiltStateAndRemainIndependent()
    {
        using var template = new WorktreeSetupTemplate();
        var reference = CreateReducedAcceptanceCohortRepository();
        var first = CreateReducedAcceptanceCohortRepository();
        var second = CreateReducedAcceptanceCohortRepository();
        try
        {
            // Exercise overlaying Git's immutable objects even on hosts that create writable ones.
            var objects = Directory.GetFiles(Path.Combine(first, ".git", "objects"), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(objects);
            foreach (var file in objects)
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            Builder.Manifest(reference);
            Builder.Manifest(first, template);
            Builder.Manifest(second, template);
            foreach (var file in objects)
            {
                Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
                var relative = Path.GetRelativePath(first, file);
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, relative)));
            }
            AssertEquivalent(reference, first);
            AssertEquivalent(reference, second);
            var goal = GoalId.New();
            var actualWorktree = GoalWorktrees.Ensure(first, goal);
            var expectedWorktree = GoalWorktrees.Ensure(reference, goal);
            Assert.Equal(actualWorktree, GoalWorktrees.TryResolve(first, goal));
            AssertEquivalent(reference, first);
            Assert.Equal(Read(expectedWorktree, "status", "--porcelain"), Read(actualWorktree, "status", "--porcelain"));
            File.WriteAllText(Path.Combine(first, "config", "acceptance-manifest.json"), "first copy only");
            Assert.Equal(Read(reference, "show", "HEAD:config/acceptance-manifest.json"),
                File.ReadAllText(Path.Combine(second, "config", "acceptance-manifest.json")));
            Assert.Empty(Read(second, "status", "--porcelain"));
        }
        finally { DeleteDirectory(reference); DeleteDirectory(first); DeleteDirectory(second); }
    }

    [Fact]
    public void TerminalSeedCopies_MatchReferenceAndSupportRealWorktreeCreation()
    {
        using var template = new WorktreeSetupTemplate();
        var reference = Directory.CreateTempSubdirectory("terminal-reference-").FullName;
        var first = Directory.CreateTempSubdirectory("terminal-copy-").FullName;
        var second = Directory.CreateTempSubdirectory("terminal-copy-").FullName;
        try
        {
            WorktreeSetupTemplate.HandBuildTerminalSeed(reference);
            template.CopyTerminalSeed(first);
            template.CopyTerminalSeed(second);
            AssertEquivalent(reference, first);
            AssertEquivalent(reference, second);
            Assert.Equal(Read(reference, "rev-parse", "HEAD"), Read(first, "rev-parse", "HEAD"));
            var goal = GoalId.New();
            var expected = GoalWorktrees.Ensure(reference, goal);
            var actual = GoalWorktrees.Ensure(first, goal);
            AssertEquivalent(reference, first);
            Assert.Equal(actual, GoalWorktrees.TryResolve(first, goal));
            Assert.Equal(Read(expected, "symbolic-ref", "HEAD"), Read(actual, "symbolic-ref", "HEAD"));
            File.WriteAllText(Path.Combine(actual, "copy-only.txt"), "isolated");
            Assert.False(File.Exists(Path.Combine(second, "copy-only.txt")));
            Assert.False(Directory.Exists(Path.Combine(second, GoalWorktrees.DirectoryName)));
        }
        finally { DeleteDirectory(reference); DeleteDirectory(first); DeleteDirectory(second); }
    }

    private static void AssertEquivalent(string expected, string actual)
    {
        Assert.Equal(Read(expected, "for-each-ref", "--format=%(refname) %(tree) %(subject)"),
            Read(actual, "for-each-ref", "--format=%(refname) %(tree) %(subject)"));
        Assert.Equal(Read(expected, "log", "--all", "--format=%T %s"), Read(actual, "log", "--all", "--format=%T %s"));
        foreach (var args in new[] { new[] { "ls-files", "-s" }, ["status", "--porcelain"],
            ["symbolic-ref", "HEAD"], ["config", "--local", "--list"] })
            Assert.Equal(Read(expected, args), Read(actual, args));
        string Worktrees(string root) => Read(root, "worktree", "list", "--porcelain")
            .Replace(root.Replace('\\', '/'), "<root>", StringComparison.Ordinal)
            .Split('\n').Where(line => !line.StartsWith("HEAD ", StringComparison.Ordinal)).Aggregate("", (a, b) => a + b + "\n");
        Assert.Equal(Worktrees(expected), Worktrees(actual));
        var expectedExclude = Path.Combine(expected, ".git", "info", "exclude");
        Assert.Equal(File.ReadAllBytes(expectedExclude), File.ReadAllBytes(Path.Combine(actual, ".git", "info", "exclude")));
        Assert.Equal(TrackedFiles(expected), TrackedFiles(actual));
    }

    private static string[] TrackedFiles(string root) => Read(root, "ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(Path.Combine(root, path)))).ToArray();

    private static string Read(string root, params string[] args)
    {
        var result = GitCli.Run(root, args);
        Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private sealed class Builder : AcceptanceCohortWorkflowTests
    {
        internal static void Manifest(string root, WorktreeSetupTemplate? template = null) => AddAcceptanceManifest(root, template);
    }
}
