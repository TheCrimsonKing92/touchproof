using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Kind = GoalBranchScenarioTemplate.BaseKind;

// Parallel-safe: every test owns its copies; recipes are immutable. Observer wrappers forward
// other observers and count only their own async flow, without a serial collection/lane.
public sealed class GoalBranchScenarioTemplateTests(ITestOutputHelper output) : GoalWorktreeTestBase
{
    private const int ConcurrentFirstUseHangFailsafeMilliseconds = 120_000;
    private const string Feature = "src/feature.txt";
    private const string Content = "goal work";
    private const string Message = "Goal work";

    [Fact]
    public void Copy_SameRecipe_IsolatesFilesBranchesCommitsAndTemplate()
    {
        var template = GoalBranchScenarioTemplate.TemplateDirectory(Kind.GoalWorktreeDefault, Feature, Content, Message);
        var before = Snapshot(template);
        var first = CreateSeededRepository();
        var second = CreateSeededRepository();
        try
        {
            var firstId = GoalId.New();
            var secondId = GoalId.New();
            var a = CreateGoalBranchScenario(first, firstId, Feature, Content).Worktree;
            var b = CreateGoalBranchScenario(second, secondId, Feature, Content).Worktree;
            Assert.NotEqual(first, second);
            AssertLinks(first, a);
            AssertLinks(second, b);
            var originalTip = RunGitOutput(b, "rev-parse", "HEAD").Trim();
            RunGit(a, "checkout", "-b", "copy-only");
            File.WriteAllText(Path.Combine(a, "copy-only.txt"), "isolated");
            RunGit(a, "add", "-A");
            RunGit(a, "commit", "-m", "Only in first copy");
            var changedTip = RunGitOutput(a, "rev-parse", "HEAD").Trim();
            Assert.NotEqual(originalTip, changedTip);
            Assert.False(File.Exists(Path.Combine(b, "copy-only.txt")));
            Assert.False(BranchExists(second, "copy-only"));
            Assert.NotEqual(0, RunGitExitCode(second, "cat-file", "-e", changedTip));
            Assert.Equal(originalTip, RunGitOutput(b, "rev-parse", "HEAD").Trim());
            Assert.False(BranchExists(first, GoalWorktrees.BranchName(secondId)));
            Assert.False(BranchExists(second, GoalWorktrees.BranchName(firstId)));
            Assert.Equal(before, Snapshot(template));
        }
        finally { DeleteDirectory(first); DeleteDirectory(second); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Copy_MatchesHandBuiltGoalBranchProperties(int baseKind)
    {
        var kind = (Kind)baseKind;
        var copy = CreateBase(kind);
        var handmade = CreateBase(kind);
        try
        {
            var id = GoalId.New();
            var worktree = GoalBranchScenarioTemplate.Materialize(copy, kind, id, Feature, Content, Message);
            var expected = HandBuild(handmade, id, Feature, Content, Message);
            AssertProperties(copy, worktree, id, kind);
            AssertProperties(handmade, expected, id, kind);
            Assert.Equal(RunGitOutput(handmade, "rev-parse", "HEAD").Trim(),
                RunGitOutput(copy, "rev-parse", "HEAD").Trim());
            Assert.Equal(RunGitOutput(expected, "show", "HEAD:" + Feature), RunGitOutput(worktree, "show", "HEAD:" + Feature));
            var oldPrefix = GoalWorktrees.BranchName(GoalBranchScenarioTemplate.TemplateGoalId)[5..];
            Assert.DoesNotContain(Directory.EnumerateDirectories(copy, "*", SearchOption.AllDirectories),
                directory => Path.GetFileName(directory) == oldPrefix);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(copy, ".git"), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(copy, ".git"), file);
                if (relative.StartsWith("objects" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    Path.GetFileName(file) == "index") continue;
                Assert.DoesNotContain(oldPrefix, File.ReadAllText(file));
            }
        }
        finally { DeleteDirectory(copy); DeleteDirectory(handmade); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Copy_WarmRecipe_LaunchesAtMostTwoGitProcesses(int baseKind) =>
        AssertLaunchSaving((Kind)baseKind, Feature, Content, Message);

    [Fact]
    public void TerminalSweepScenarioSetup_LaunchesFewerGitProcessesThanHandBuilt() =>
        AssertLaunchSaving(Kind.CliAcceptance, "src/reappeared.txt", Content, Message);

    [Fact]
    public void CommitGoalWork_WarmPristineRoot_LaunchesAtMostTwoGitProcesses() =>
        AssertLaunchSaving(Kind.CliAcceptance, "src/reappeared.txt", Content, Message, useCommitHelper: true);

    [Fact]
    public void AcceptanceLandingScenarioSetup_LaunchesFewerGitProcessesThanHandBuilt() =>
        AssertLaunchSaving(Kind.GoalWorktreeDefault, "feature.txt", Content, Message);

    [Fact]
    public void RebaseMergeScenarioSetup_LaunchesFewerGitProcessesThanHandBuilt() =>
        AssertLaunchSaving(Kind.GoalWorktreeDefault, "blocked-feature.txt", "must not land", "Blocked goal work");

    [Fact(Timeout = ConcurrentFirstUseHangFailsafeMilliseconds)] // Hang detector: both concurrent first-use copies must finish.
    public async Task Copy_ConcurrentFirstUse_BuildsOneTemplate()
    {
        var warmBase = CreateSeededRepository();
        DeleteDirectory(warmBase);
        var path = "concurrent-" + Guid.NewGuid().ToString("n") + ".txt";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var roots = new System.Collections.Concurrent.ConcurrentBag<string>();
        try
        {
            using var launches = new Launches();
            var copies = Enumerable.Range(0, 2).Select(async _ =>
            {
                await start.Task;
                return await Task.Run(() =>
                {
                    var root = CreateSeededRepository();
                    roots.Add(root);
                    CreateGoalBranchScenario(root, GoalId.New(), path, Content);
                    return GoalBranchScenarioTemplate.TemplateDirectory(Kind.GoalWorktreeDefault, path, Content, Message);
                });
            }).ToArray();
            start.SetResult();
            var templates = await Task.WhenAll(copies);
            Assert.Single(templates.Distinct());
            Assert.Equal(1, launches.Commits);
            Assert.Equal(2, roots.Distinct().Count());
        }
        finally { foreach (var root in roots) DeleteDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitGoalWork_NonPristineRoot_FallsBackToHandBuilt(bool existingWorktree)
    {
        var cli = new CliSetup();
        var root = cli.Create();
        try
        {
            var id = GoalId.New();
            string expectedParent;
            if (existingWorktree)
            {
                var initial = cli.Commit(root, id, "first.txt", "first", useTemplate: false);
                expectedParent = RunGitOutput(initial, "rev-parse", "HEAD").Trim();
            }
            else
            {
                File.WriteAllText(Path.Combine(root, "advanced.txt"), "advanced main");
                RunGit(root, "add", "-A");
                RunGit(root, "commit", "-m", "Advance main");
                expectedParent = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            }
            var state = Path.Combine(root, ".orchestrator", "caller-state.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            File.WriteAllBytes(state, [1, 2, 3, 4]);
            var worktree = cli.Commit(root, id, "next.txt", "next");
            Assert.Equal(expectedParent, RunGitOutput(worktree, "rev-parse", "HEAD^").Trim());
            Assert.Equal("next", File.ReadAllText(Path.Combine(worktree, "next.txt")));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(state));
            AssertLinks(root, worktree);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Copy_PreservesCallerStateAndRejectsNonPristineRoot()
    {
        var root = CreateSeededRepository();
        try
        {
            var state = Path.Combine(root, ".orchestrator", "caller-state.bin");
            File.WriteAllBytes(state, [4, 3, 2, 1]);
            var id = GoalId.New();
            CreateGoalBranchScenario(root, id, Feature, Content);
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(state));
            var before = Snapshot(root);
            Assert.Throws<InvalidOperationException>(() => CreateGoalBranchScenario(root, GoalId.New(), Feature, Content));
            Assert.Equal(before, Snapshot(root));
        }
        finally { DeleteDirectory(root); }
    }

    private void AssertLaunchSaving(Kind kind, string path, string content, string message, bool useCommitHelper = false)
    {
        GoalBranchScenarioTemplate.TemplateDirectory(kind, path, content, message);
        var roots = new List<string>();
        try
        {
            var id = GoalId.New();
            int copied, copyProbes, handmade, handProbes;
            using (var count = new Launches())
            using (var probes = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                var root = CreateBase(kind);
                roots.Add(root);
                if (kind == Kind.CliAcceptance)
                {
                    var cli = new CliSetup();
                    if (useCommitHelper) cli.Commit(root, id, path, content);
                    else cli.Scenario(root, id, path, content, message);
                }
                else CreateGoalBranchScenario(root, id, path, content, message, kind);
                copied = count.Count;
                copyProbes = probes.Count;
            }
            using (var count = new Launches())
            using (var probes = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                var root = CreateBase(kind);
                roots.Add(root);
                if (kind == Kind.CliAcceptance) new CliSetup().Commit(root, id, path, content, useTemplate: false);
                else HandBuild(root, id, path, content, message);
                handmade = count.Count;
                handProbes = probes.Count;
            }
            output.WriteLine($"{kind} {path}: copy={copied} (probes={copyProbes}), hand-built={handmade} (probes={handProbes}), saved={handmade - copied}");
            Assert.InRange(copied, 1, 2);
            Assert.True(handmade > 2, $"Hand-built setup must exceed the copy bound: {handmade}");
            Assert.True(handmade > copied);
            Assert.InRange(copyProbes, 1, copied);
            Assert.InRange(handProbes, 1, handmade);
        }
        finally { foreach (var root in roots) DeleteDirectory(root); }
    }

    private static string CreateBase(Kind kind) => kind == Kind.CliAcceptance
        ? new CliSetup().Create()
        : kind == Kind.GoalWorktreeMain ? CreateReducedAcceptanceCohortRepository() : CreateSeededRepository();

    private static string HandBuild(string root, GoalId id, string path, string content, string message)
    {
        var worktree = GoalWorktrees.Ensure(root, id);
        var file = Path.Combine(worktree, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        RunGit(worktree, "add", "-A");
        RunGit(worktree, "commit", "-m", message);
        return worktree;
    }

    private static void AssertProperties(string root, string worktree, GoalId id, Kind kind)
    {
        var branch = GoalWorktrees.BranchName(id);
        Assert.True(BranchExists(root, branch));
        Assert.Equal(branch, RunGitOutput(worktree, "symbolic-ref", "--short", "HEAD").Trim());
        Assert.Equal(RunGitOutput(root, "rev-parse", branch).Trim(), RunGitOutput(worktree, "rev-parse", "HEAD").Trim());
        var main = RunGitOutput(root, "rev-parse", "HEAD").Trim();
        Assert.Equal(main, RunGitOutput(root, "show", "-s", "--format=%P", branch).Trim());
        Assert.Equal(Content, File.ReadAllText(Path.Combine(worktree, Feature)));
        Assert.Equal(Content, RunGitOutput(worktree, "show", "HEAD:" + Feature));
        Assert.Equal(Message, RunGitOutput(worktree, "log", "-1", "--format=%B").Trim());
        Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--porcelain").Trim());
        Assert.Contains("worktree " + worktree.Replace('\\', '/'), RunGitOutput(root, "worktree", "list", "--porcelain"));
        Assert.Equal("tests@example.com", RunGitOutput(root, "config", "user.email").Trim());
        Assert.Equal(kind == Kind.CliAcceptance ? "CLI Tests" : "Worktree Tests", RunGitOutput(root, "config", "user.name").Trim());
        AssertLinks(root, worktree);
    }

    private static void AssertLinks(string root, string worktree)
    {
        var gitFile = Path.Combine(worktree, ".git");
        var gitDir = File.ReadAllText(gitFile).Trim()["gitdir: ".Length..];
        var administrativePath = Path.GetFullPath(gitDir);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, administrativePath);
        Assert.Equal(Path.GetFullPath(gitFile), Path.GetFullPath(File.ReadAllText(Path.Combine(administrativePath, "gitdir")).Trim()));
        Assert.Equal("../..", File.ReadAllText(Path.Combine(administrativePath, "commondir")).Trim());
    }

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/" :
            ":" + new FileInfo(path).Length + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class CliSetup : CliCommandTestBase
    {
        internal string Create() => CreateAcceptanceRepository();
        internal string Commit(string root, GoalId id, string path, string content, bool useTemplate = true) =>
            CommitGoalWork(root, id, path, content, useTemplate);
        internal string Scenario(string root, GoalId id, string path, string content, string message) =>
            CreateGoalBranchScenario(root, id, path, content, message).Worktree;
    }

    private sealed class Launches : IDisposable
    {
        private readonly AsyncLocal<bool> active = new();
        private readonly Action<string>? previous;
        private readonly Action<string> observer;
        private int count, commits;
        internal int Count => Volatile.Read(ref count);
        internal int Commits => Volatile.Read(ref commits);
        internal Launches()
        {
            Action<string>? prior;
            Action<string> wrapper;
            do
            {
                prior = Volatile.Read(ref GitCli.LaunchObserver);
                var captured = prior;
                wrapper = command =>
                {
                    if (active.Value)
                    {
                        Interlocked.Increment(ref count);
                        if (command == "commit") Interlocked.Increment(ref commits);
                    }
                    captured?.Invoke(command);
                };
            } while (!ReferenceEquals(Interlocked.CompareExchange(ref GitCli.LaunchObserver, wrapper, prior), prior));
            previous = prior;
            observer = wrapper;
            active.Value = true;
        }
        public void Dispose()
        {
            active.Value = false;
            // Another observer may have stacked on ours; keep its forwarding chain intact.
            Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer);
        }
    }
}
