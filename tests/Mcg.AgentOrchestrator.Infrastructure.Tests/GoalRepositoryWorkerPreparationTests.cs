using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Every fixture owns its directories and labeler; no process-global sandbox overrides are used.
public sealed class GoalRepositoryWorkerPreparationTests
{
    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void DispatchPreparesEveryRepositoryBeforeMaterialization(AgentRole role)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var targets = GoalRepositoryWorkerPreparation.ResolveTargets(fixture.Goal, role);
        fixture.Apply(targets, writable: true);

        Assert.Equal(2, targets.Count);
        Assert.Contains("prepare-repository-worktrees", fixture.Phases);
        Assert.True(fixture.Phases.IndexOf("prepare-repository-worktrees") < fixture.Phases.IndexOf("materialize-sandbox"));
        foreach (var target in targets)
        {
            Assert.Contains((target.WorktreePath, "(OI)(CI)L", true), fixture.Labeler.Sets);
            Assert.True(File.Exists(Path.Combine(target.WorktreePath, WorkerSandboxPreparer.MarkerFileName)));
            var gitFile = Path.Combine(target.WorktreePath, ".git");
            Assert.Contains((gitFile, "M", false), fixture.Labeler.Sets);
            Assert.True(fixture.Labeler.Query(gitFile).Medium);
            Assert.False(fixture.Labeler.Query(gitFile).Low);
            Assert.DoesNotContain(target.CheckoutRoot, fixture.Labeler.Queries);
            Assert.DoesNotContain(fixture.Labeler.Sets, call => call.Path == target.CheckoutRoot);
            Assert.DoesNotContain(fixture.Labeler.Queries, path => path.StartsWith(Path.Combine(target.CheckoutRoot, ".git")));
            Assert.DoesNotContain(fixture.Labeler.Sets, call => call.Path.StartsWith(Path.Combine(target.CheckoutRoot, ".git")));
        }
    }

    [Theory]
    [InlineData(AgentRole.Reviewer)]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void ReadOnlyDispatchDoesNotLabelRepositoryWorktrees(AgentRole role)
    {
        using var fixture = new Fixture();
        Assert.Empty(GoalRepositoryWorkerPreparation.ResolveTargets(fixture.Goal, role));
        if (!OperatingSystem.IsWindows()) return;
        // Even a transported target cannot bypass the host's write-capable gate.
        fixture.Apply(GoalRepositoryWorkerPreparation.ResolveTargets(fixture.Goal, AgentRole.Developer), writable: false);
        foreach (var root in fixture.Goal.Repositories!.CheckoutRoots.Values.Where(root => root != fixture.Home))
        {
            Assert.DoesNotContain(fixture.Labeler.Sets, call => call.Path.StartsWith(root));
            Assert.DoesNotContain(fixture.Labeler.Queries, path => path.StartsWith(root));
        }
    }

    [Theory]
    [InlineData("checkout")]
    [InlineData("common")]
    [InlineData("ancestor")]
    [InlineData("missing")]
    public void InvalidTargetIsRefusedBeforeAnyLabelCall(string kind)
    {
        using var fixture = new Fixture();
        var target = GoalRepositoryWorkerPreparation.ResolveTargets(fixture.Goal, AgentRole.Developer)[0];
        var path = kind switch
        {
            "checkout" => target.CheckoutRoot,
            "common" => Path.Combine(target.CheckoutRoot, ".git"),
            "ancestor" => Path.GetDirectoryName(target.CheckoutRoot)!,
            _ => target.WorktreePath
        };
        if (kind == "missing") File.Delete(Path.Combine(path, ".git"));
        Assert.ThrowsAny<InvalidOperationException>(() => GoalRepositoryWorkerPreparation.Prepare(
            [target with { WorktreePath = path }], fixture.Goal.Id, fixture.Sandbox,
            new WorkerSandboxPreparer(fixture.Labeler), fixture.Labeler, fixture.FileSystem));
        Assert.Empty(fixture.Labeler.Sets);
        Assert.Empty(fixture.Labeler.Queries);
    }

    [Fact]
    public void FailedRepositoryLabelPreventsMaterialization()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var targets = GoalRepositoryWorkerPreparation.ResolveTargets(fixture.Goal, AgentRole.Developer);
        fixture.Labeler.RefusedPath = targets[0].WorktreePath;
        Assert.Throws<InvalidOperationException>(() => fixture.Apply(targets, writable: true));
        Assert.DoesNotContain("materialize-sandbox", fixture.Phases);
    }

    [Fact]
    public void NoDeclarationKeepsRepositoryParametersAbsent()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Undeclared", []);
        Assert.Empty(GoalRepositoryWorkerPreparation.ResolveTargets(goal, AgentRole.Developer));
        var file = Path.Combine(fixture.Home, "parameters.json");
        DispatchProcessHost.WriteParameters(file, new("command", fixture.Home, "stdout", "stderr", "exit", null, false));
        Assert.DoesNotContain("repositoryWorktrees", File.ReadAllText(file));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "mcg-repository-prep", Guid.NewGuid().ToString("N"));
        public string Home { get; }
        public string Sandbox => Path.Combine(Home, ".mcg-sandbox");
        public Goal Goal { get; }
        public Labeler Labeler { get; } = new();
        public IWorkerSandboxFileSystem FileSystem { get; } = new FixtureFileSystem();
        public List<string> Phases { get; } = [];

        public Fixture()
        {
            Home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var kernel = new AgentOrchestratorKernel();
            Goal = kernel.CreateGoal("Prepare repositories", []);
            var roots = new Dictionary<string, string> { ["default"] = Home };
            foreach (var key in new[] { "second", "third" })
            {
                roots[key] = Directory.CreateDirectory(Path.Combine(root, key)).FullName;
                var common = Directory.CreateDirectory(Path.Combine(roots[key], ".git")).FullName;
                var worktree = Directory.CreateDirectory(GoalRepositoryWorktreeLayout.WorktreePath(roots[key], Goal.Id, key)).FullName;
                File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(common, "worktrees", key));
            }
            kernel.DeclareGoalRepositories(Goal.Id, new GoalRepositoryDeclaration(roots.Keys.ToArray(), roots));
        }

        public void Apply(IReadOnlyList<GoalRepositoryWorkerPreparation.Target> targets, bool writable)
        {
            var info = new ProcessStartInfo { WorkingDirectory = Home, UseShellExecute = false };
            info.Environment.Clear();
            info.Environment["PATH"] = string.Empty;
            info.ArgumentList.Add("Write-Output ok");
            var parameters = new DispatchProcessHost.DispatchRunParameters("command", Home, "stdout", Path.Combine(root, "stderr"),
                "exit", null, false, SandboxLowIntegrity: true, SandboxWorktreeWritable: writable,
                PrepGoalId: Goal.Id.Value, RepositoryWorktrees: targets);
            DispatchProcessHost.ApplyWorkerSandbox(info, parameters, new WorkerSandboxPreparer(Labeler),
                (phase, _, _) => Phases.Add(phase), protectWorkspaceBoundary: _ => { }, protectGitMetadata: _ => { },
                providerEnvironmentReader: _ => null, gitMetadataLabeler: Labeler, sandboxFileSystem: FileSystem);
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class FixtureFileSystem : IWorkerSandboxFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);
    }

    private sealed class Labeler : IWorkerIntegrityLabeler
    {
        public List<(string Path, string Level, bool Recursive)> Sets { get; } = [];
        public List<string> Queries { get; } = [];
        private readonly Dictionary<string, IntegrityLabelState> states = new(StringComparer.OrdinalIgnoreCase);
        public string? RefusedPath { get; set; }
        public IntegrityLabelState Query(string path)
        {
            Queries.Add(path);
            return states.GetValueOrDefault(path, new(true, false, false, Medium: true));
        }
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Sets.Add((path, level, recursive));
            if (path == RefusedPath) return false;
            states[path] = new(true, level.Contains('L'), level.Contains("(CI)"), Medium: level == "M");
            return true;
        }
    }
}
