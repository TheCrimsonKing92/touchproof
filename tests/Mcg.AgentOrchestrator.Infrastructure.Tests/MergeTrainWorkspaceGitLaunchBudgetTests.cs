using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static GitWorkspaceLaunchFixture;

// Parallel-safe: repositories are test-owned; recording and labeler overrides are thread-local.
public sealed class MergeTrainWorkspaceGitLaunchBudgetTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void Create_MergeBearingMember_UsesExactLaunchBudgetAndSeparateDisposal()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            WriteAuthority(repo, "new SourceClassCeiling(\"AbsentLaunchProbe\", 10, 1)\n");
            var first = CreateMergeCandidate(repo);
            var second = GoalId.New();
            var secondRevision = CreateWorktreeCandidate(repo, second, "second.txt", "second\n");
            var main = RunGitOutput(repo, "rev-parse", "main");
            Assert.Equal("1", RunGitOutput(repo, "rev-list", "--merges", "--count",
                $"{main}..{first.Revision}"));
            using var labeler = AcceptanceWorkspaceIntegrityPreparer.PushIntegrityLabelerForTests(new Labeler());
            MergeTrainWorkspace workspace;
            using (var creation = new Recording())
            {
                workspace = GoalWorktrees.CreateMergeTrainWorkspace(repo, main,
                    [TrainBind(first.Goal, first.Revision, "first.txt", "first"),
                     TrainBind(second, secondRevision, "second.txt", "second")], "main");
                using (workspace)
                {
                    AssertBudget(creation, new()
                    {
                        ["worktree add"] = 1, ["merge-base"] = 2, ["rev-list"] = 2,
                        ["log"] = 1, ["commit-tree"] = 1, ["rebase"] = 2,
                        ["show"] = 1, ["status"] = 1
                    });
                    Assert.Empty(workspace.Ejections);
                    Assert.Equal(new[] { first.Goal, second }, workspace.Members.Select(member => member.GoalId));
                    Assert.Equal("0", RunGitOutput(repo, "rev-list", "--merges", "--count",
                        $"{main}..{workspace.CommitRevision}"));
                    Assert.Equal("first\n", File.ReadAllText(Path.Combine(workspace.Path, "first.txt")));
                    Assert.Equal("second\n", File.ReadAllText(Path.Combine(workspace.Path, "second.txt")));
                    using var disposal = new Recording();
                    workspace.Dispose();
                    AssertBudget(disposal, new() { ["worktree remove"] = 1 });
                    Assert.Equal(["worktree", "remove", "--force", workspace.Path], Assert.Single(disposal.Arguments));
                    Assert.False(Directory.Exists(workspace.Path));
                }
            }
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void Retighten_ExceededClassAndDirtySource_ResolvesMainWithoutLaunch()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            Commit(repo, "src/LaunchProbeWidget.cs", "partial class LaunchProbeWidget\n{\n    int value;\n}\n", "Add probe");
            const string authority = "new SourceClassCeiling(\"LaunchProbeWidget\", 2, 1)\n";
            WriteAuthority(repo, authority);
            var main = RunGitOutput(repo, "rev-parse", "main");
            var measurements = SourceSizeRatchet.MeasureClasses(repo, ["LaunchProbeWidget"], out var unreadable);
            Assert.Empty(unreadable);
            Assert.True(measurements["LaunchProbeWidget"].TotalLines > 2);
            Assert.Equal(1, measurements["LaunchProbeWidget"].PartialFiles);
            File.WriteAllText(Path.Combine(repo, "src", "untracked.txt"), "dirty\n");
            Assert.Contains("src/untracked.txt", RunGitOutput(repo, "status", "--porcelain", "--untracked-files=all", "--", "src"));
            using (var recording = new Recording())
            {
                var rows = SourceSizeRatchetRetightener.RetightenAndCommit(repo, main, "probe");
                AssertBudget(recording, new() { ["show"] = 1, ["status"] = 2 });
                Assert.Empty(rows);
            }
            Assert.Equal(main, RunGitOutput(repo, "rev-parse", "HEAD"));
            Assert.Equal(authority, File.ReadAllText(Path.Combine(repo, SourceSizeRatchet.SourcePath)));
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SquashMessage_DeveloperSubjects_PreservesLiteralBytes(bool mergeSuppliesSubject)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var goal = new GoalId("11111111111111111111111111111111");
            var path = GoalWorktrees.Ensure(repo, goal);
            Commit(path, "first.txt", "first\n", "Developer(11111111): first change");
            var first = RunGitOutput(path, "log", "-1", "--format=%h");
            Commit(path, "second.txt", "second\n", "Developer(11111111):  keep extra space\tand punctuation $; | & `");
            var second = RunGitOutput(path, "log", "-1", "--format=%h");
            Commit(path, "foreign.txt", "foreign\n", "Developer(22222222): ignore this goal");
            var foreign = RunGitOutput(path, "log", "-1", "--format=%h");
            Commit(repo, "main-only.txt", "main\n", "Main-only change");
            var mainHead = RunGitOutput(repo, "rev-parse", "main");
            RunGit(path, "merge", "--no-ff", "-m", mergeSuppliesSubject
                ? "Developer(11111111): merge subject" : "Merge main", "main");
            var oldHead = RunGitOutput(path, "rev-parse", "HEAD");
            var tree = RunGitOutput(path, "rev-parse", "HEAD^{tree}");
            Assert.Equal("1", RunGitOutput(path, "rev-list", "--merges", "--count", $"{mainHead}..{oldHead}"));
            // These literal templates are independent of the combined-log parser and formatting.
            var expected = mergeSuppliesSubject
                ? $"Developer(11111111): merge subject\n\nSquashed onto main from {oldHead} (tree {tree}).\n- {first} Developer(11111111): first change\n- {second} Developer(11111111):  keep extra space\tand punctuation $; | & `\n- {foreign} Developer(22222222): ignore this goal\n\nGoal: 11111111111111111111111111111111"
                : $"Developer(11111111):  keep extra space\tand punctuation $; | & `\n\nSquashed onto main from {oldHead} (tree {tree}).\n- {first} Developer(11111111): first change\n- {second} Developer(11111111):  keep extra space\tand punctuation $; | & `\n- {foreign} Developer(22222222): ignore this goal\n\nGoal: 11111111111111111111111111111111";
            using var recording = new Recording();
            var actual = GoalWorktrees.ComposeSquashCommitMessage(repo, "main", mainHead, oldHead, tree, goal);
            AssertBudget(recording, new() { ["log"] = 1 });
            Assert.Equal(expected, actual);
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void SquashMessage_NoDeveloperSubject_PreservesLiteralFallbackAndBullets()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main");
            Commit(repo, "first.txt", "first\n", "Ordinary subject");
            var first = RunGitOutput(repo, "log", "-1", "--format=%h");
            Commit(repo, "second.txt", "second\n", "developer(11111111): wrong case");
            var second = RunGitOutput(repo, "log", "-1", "--format=%h");
            var oldHead = RunGitOutput(repo, "rev-parse", "HEAD");
            var tree = RunGitOutput(repo, "rev-parse", "HEAD^{tree}");
            var goal = new GoalId("11111111111111111111111111111111");
            var expected = $"Developer(11111111): squashed goal branch\n\nSquashed onto main from {oldHead} (tree {tree}).\n- {first} Ordinary subject\n- {second} developer(11111111): wrong case\n\nGoal: 11111111111111111111111111111111";
            using var recording = new Recording();
            var actual = GoalWorktrees.ComposeSquashCommitMessage(repo, "main", main, oldHead, tree, goal);
            AssertBudget(recording, new() { ["log"] = 1 });
            Assert.Equal(expected, actual);
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SquashMessage_EmptyOrFailedRange_PreservesLiteralFallback(bool invalid)
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var main = RunGitOutput(repo, "rev-parse", "main");
            var oldHead = invalid ? "missing-revision" : main;
            var tree = RunGitOutput(repo, "rev-parse", "HEAD^{tree}");
            var goal = new GoalId("11111111111111111111111111111111");
            var expected = $"Developer(11111111): squashed goal branch\n\nSquashed onto main from {oldHead} (tree {tree}).\n\nGoal: 11111111111111111111111111111111";
            using var recording = new Recording();
            var actual = GoalWorktrees.ComposeSquashCommitMessage(repo, "main", main, oldHead, tree, goal);
            AssertBudget(recording, new() { ["log"] = 1 });
            Assert.Equal(expected, actual);
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void TryCreate_FileResolverMiss_FallsBackToSameSquashCommit()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            var member = CreateMergeCandidate(repo);
            var main = RunGitOutput(repo, "rev-parse", "main");
            var treeExpression = $"{member.Revision}^{{tree}}";
            Assert.True(GitRefFileResolver.TryResolve(repo, treeExpression, out var tree));
            var environment = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_DATE"] = "2026-01-01T00:00:00 +0000",
                ["GIT_COMMITTER_DATE"] = "2026-01-01T00:00:00 +0000"
            };
            string? Create() => MainIntegrationSquash.TryCreate(repo, main, main,
                member.Revision, member.Goal, args => GitCli.RunWithEnvironment(repo, environment, args));
            string? hit;
            using (var recording = new Recording())
            {
                hit = Create();
                AssertBudget(recording, new() { ["rev-list"] = 1, ["log"] = 1, ["commit-tree"] = 1 });
            }
            Assert.NotNull(hit);
            var replacements = Path.Combine(CommonDirectory(repo), "refs", "replace");
            Directory.CreateDirectory(replacements);
            try
            {
                Assert.False(GitRefFileResolver.TryResolve(repo, treeExpression, out _));
                using var recording = new Recording();
                var miss = Create();
                // HasMergeCommitsOutsideBase is memoized from the first call.
                AssertBudget(recording, new() { ["rev-parse"] = 1, ["log"] = 1, ["commit-tree"] = 1 });
                Assert.Equal(["rev-parse", "--verify", treeExpression],
                    Assert.Single(recording.Arguments.Where(args => GitCli.SubcommandKey(args) == "rev-parse")));
                Assert.Equal(hit, miss);
            }
            finally { Directory.Delete(replacements); }
            Assert.Equal(tree, RunGitOutput(repo, "rev-parse", $"{hit}^{{tree}}"));
        }
        finally { DeleteDirectory(repo); }
    }

    private static void AssertBudget(Recording recording, Dictionary<string, int> expected)
    {
        recording.Close(null); // Checks the LaunchObserver census against the full argv capture.
        var actual = recording.Arguments.GroupBy(args => GitCli.SubcommandKey(args))
            .ToDictionary(group => group.Key, group => group.Count());
        TestContext.Current.TestOutputHelper!.WriteLine(string.Join("\n",
            recording.Arguments.Select(args => string.Join(" ", args))));
        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }

    private static (GoalId Goal, string Revision) CreateMergeCandidate(string repo)
    {
        RunGit(repo, "config", "core.autocrlf", "false");
        var goal = GoalId.New();
        _ = CreateWorktreeCandidate(repo, goal, "first.txt", "first\n");
        var path = GoalWorktrees.Ensure(repo, goal);
        Commit(repo, "main-only.txt", "main\n", "Advance main");
        RunGit(path, "merge", "--no-ff", "-m", "Integrate main", "main");
        return (goal, RunGitOutput(path, "rev-parse", "HEAD"));
    }

    private static void WriteAuthority(string repo, string contents) =>
        Commit(repo, SourceSizeRatchet.SourcePath, contents, "Add source size authority");

    private static void Commit(string repo, string relativePath, string contents, string subject)
    {
        var path = Path.Combine(repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        RunGit(repo, "add", relativePath);
        RunGit(repo, "commit", "-m", subject);
    }
}
