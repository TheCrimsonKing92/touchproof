using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture repositories, linked checkouts and Git readback roots are private to each test.
public sealed class WorkerBuildSeedTests
{
    private const string ManifestRules = "!.orchestrator/\n.orchestrator/*\n!.orchestrator/acceptance-manifest.json\n";
    private const string SeedExcludes = ".orchestrator/\n.orchestrator-context/\nbin/\nobj/\n";
    private static readonly string[] ExcludedPaths =
        [".orchestrator/runtime.txt", ".orchestrator-context/runtime.txt", "src/Feature/bin/output.txt", "src/Feature/obj/output.txt"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportedSeedMatchesGitAndKeepsRealLinkedCheckout(bool customSeed)
    {
        var measured = FixtureGitLaunchBudget.Measure("worker-build supported seed", () =>
            new WorkerBuildRequestFixture(extraProject: customSeed, linkedWorktree: true,
                seedRepository: customSeed ? Customize : null));
        using var fixture = measured.Value;
        AssertSetupLaunches(measured.Counts, 0);
        Assert.Equal(1, measured.Counts.Count("worktree"));

        var main = Path.Combine(fixture.Root, "repository");
        var linked = fixture.RepositoryRoot;
        var oracle = Path.Combine(fixture.Root, "git-oracle");
        Write(oracle, "NuGet.Config", "<configuration><packageSources><clear /></packageSources></configuration>");
        Write(oracle, "Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <RestoreConfigFile>$(MSBuildThisFileDirectory)NuGet.Config</RestoreConfigFile>
              </PropertyGroup>
            </Project>
            """);
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
        Write(oracle, WorkerBuildRequestFixture.Project, project);
        Write(oracle, WorkerBuildRequestFixture.Source, "public sealed class Feature { }");
        if (customSeed)
        {
            Write(oracle, WorkerBuildRequestFixture.OtherProject, project);
            Write(oracle, WorkerBuildRequestFixture.OtherSource, "public sealed class Other { }");
            Customize(oracle);
        }

        Git(oracle, "init", "-b", "main");
        Git(oracle, "config", "user.email", "worker-build@fixture.invalid");
        Git(oracle, "config", "user.name", "Worker build fixture");
        Write(oracle, ".git/info/exclude", SeedExcludes);
        Git(oracle, "add", ".");
        var dates = Git(main, "show", "-s", "--format=%aI%n%cI", "HEAD").Split('\n');
        DirectGit(oracle, new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = dates[0].TrimEnd('\r'),
            ["GIT_COMMITTER_DATE"] = dates[1].TrimEnd('\r')
        }, "commit", "-m", "fixture seed");

        var head = Git(main, "rev-parse", "HEAD");
        Assert.Equal(Git(oracle, "rev-parse", "HEAD"), head);
        Assert.Equal(Git(oracle, "cat-file", "commit", "HEAD"), Git(main, "cat-file", "commit", "HEAD"));
        Assert.Equal(Git(oracle, "ls-tree", "-r", "HEAD"), Git(main, "ls-tree", "-r", "HEAD"));
        Assert.Equal(Git(oracle, "ls-files", "--stage"), Git(main, "ls-files", "--stage"));
        Assert.Equal(Git(oracle, "symbolic-ref", "HEAD"), Git(main, "symbolic-ref", "HEAD"));
        Assert.Equal(SortedConfig(oracle), SortedConfig(main));
        Assert.Equal(File.ReadAllBytes(Path.Combine(oracle, ".git", "info", "exclude")),
            File.ReadAllBytes(Path.Combine(main, ".git", "info", "exclude")));
        foreach (var file in Git(oracle, "ls-files").Split('\n'))
            Assert.Equal(File.ReadAllBytes(Path.Combine(oracle, file)), File.ReadAllBytes(Path.Combine(main, file)));
        if (customSeed)
        {
            Assert.Contains(".orchestrator/acceptance-manifest.json", Git(main, "ls-files").Split('\n'));
            Assert.Equal(string.Join('\n', ExcludedPaths), Git(main, ["check-ignore", .. ExcludedPaths]));
            foreach (var path in ExcludedPaths)
                Assert.Equal(File.ReadAllBytes(Path.Combine(oracle, path)), File.ReadAllBytes(Path.Combine(main, path)));
        }
        Assert.Empty(Status(oracle));
        // The registered linked checkout lives under main as fixture runtime state.
        Assert.Empty(Git(main, "status", "--porcelain=v1", "--untracked-files=all",
            "--", ".", $":(top,exclude){GoalWorktreeLayout.DirectoryName}"));
        Git(main, "fsck", "--strict");

        var branch = GoalWorktreeLayout.BranchName(fixture.GoalId);
        Assert.Equal(head, Git(main, "rev-parse", "refs/heads/main"));
        Assert.Equal(head, Git(main, "rev-parse", $"refs/heads/{branch}"));
        Assert.Equal(head, Git(linked, "rev-parse", "HEAD"));
        Assert.Equal(branch, Git(linked, "symbolic-ref", "--short", "HEAD"));
        Assert.Contains($"worktree {linked.Replace('\\', '/')}\0HEAD {head}\0branch refs/heads/{branch}\0",
            Git(main, "worktree", "list", "--porcelain", "-z"), StringComparison.Ordinal);
        Assert.StartsWith("gitdir: ", File.ReadAllText(Path.Combine(linked, ".git")));
        var linkedMetadata = new FixtureRepositoryWriter(linked);
        Assert.NotEqual(linkedMetadata.CommonDirectory, linkedMetadata.GitDirectory);
        Assert.True(File.Exists(Path.Combine(linkedMetadata.CommonDirectory, "index")));
        Assert.True(File.Exists(Path.Combine(linkedMetadata.GitDirectory, "index")));
        Assert.Equal(Git(main, "ls-files", "--stage"), Git(linked, "ls-files", "--stage"));
        Assert.Equal($" M {WorkerBuildRequestFixture.Source}", Status(linked));
        var dispatch = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId).LastDispatch!;
        Assert.Equal(head, dispatch.BaseCommit);
        Assert.Equal(head, dispatch.WorkerBuildScope!.GoalBaseline);
    }

    [Fact]
    public void GitAttributesFallBackToRealStagingAndCommit()
    {
        const string content = "first\nsecond";
        var workingBytes = Encoding.Unicode.GetBytes(content + "\n");
        var callbacks = 0;
        var measured = FixtureGitLaunchBudget.Measure("worker-build Git fallback", () =>
            new WorkerBuildRequestFixture(seedRepository: root =>
            {
                callbacks++;
                Write(root, ".gitattributes", "*.txt working-tree-encoding=UTF-16LE text eol=lf\n");
                File.WriteAllBytes(Path.Combine(root, "converted.txt"), workingBytes);
            }));
        using var fixture = measured.Value;
        AssertSetupLaunches(measured.Counts, 1);
        Assert.Equal(1, callbacks);
        Assert.Equal(workingBytes, File.ReadAllBytes(Path.Combine(fixture.RepositoryRoot, "converted.txt")));
        Assert.Equal(content, Git(fixture.RepositoryRoot, "show", "HEAD:converted.txt"));
        Assert.Equal($" M {WorkerBuildRequestFixture.Source}", Status(fixture.RepositoryRoot));
    }

    [Fact]
    public void LiveFixturesKeepPrivateFilesIndexAndRefsThroughDisposal()
    {
        using var survivor = new WorkerBuildRequestFixture();
        var root = survivor.RepositoryRoot;
        var head = Git(root, "rev-parse", "HEAD");
        var index = Git(root, "ls-files", "--stage");
        var source = File.ReadAllBytes(Path.Combine(root, WorkerBuildRequestFixture.Source));
        string disposedRoot;
        using (var other = new WorkerBuildRequestFixture())
        {
            disposedRoot = other.Root;
            var otherRoot = other.RepositoryRoot;
            var previousHead = Git(otherRoot, "rev-parse", "HEAD");
            File.AppendAllText(Path.Combine(otherRoot, WorkerBuildRequestFixture.Source), "\n// private change");
            Git(otherRoot, "add", WorkerBuildRequestFixture.Source);
            Assert.NotEqual(index, Git(otherRoot, "ls-files", "--stage"));
            Git(otherRoot, "commit", "-m", "private change");
            Assert.NotEqual(previousHead, Git(otherRoot, "rev-parse", "HEAD"));
            Assert.Equal(head, Git(root, "rev-parse", "HEAD"));
            Assert.Equal(index, Git(root, "ls-files", "--stage"));
            Assert.Equal(source, File.ReadAllBytes(Path.Combine(root, WorkerBuildRequestFixture.Source)));
        }
        Assert.False(Directory.Exists(disposedRoot));
        Assert.Equal(head, Git(root, "rev-parse", "refs/heads/main"));
        Assert.Equal(index, Git(root, "ls-files", "--stage"));
        Git(root, "fsck", "--strict");
    }

    [Fact]
    public void ThrowingSeedCallbackCleansItsFixtureRoot()
    {
        string? fixtureRoot = null;
        var expected = new InvalidOperationException("seed callback failed");
        var actual = Assert.Throws<InvalidOperationException>(() => new WorkerBuildRequestFixture(seedRepository: root =>
        {
            fixtureRoot = Path.GetDirectoryName(root);
            throw expected;
        }));
        Assert.Same(expected, actual);
        Assert.NotNull(fixtureRoot);
        Assert.False(Directory.Exists(fixtureRoot));
    }

    private static void Customize(string root)
    {
        Write(root, WorkerBuildRequestFixture.Source, "public sealed class Feature { /* replaced */ }");
        Write(root, "src/Feature/data.txt", "additional file\n");
        Write(root, ".gitignore", ManifestRules);
        Write(root, "config/acceptance-manifest.json", "{}");
        Write(root, ".orchestrator/acceptance-manifest.json", "{}");
        foreach (var excluded in ExcludedPaths) Write(root, excluded, "retained only in the seed checkout\n");
    }

    private static void AssertSetupLaunches(FixtureGitLaunchBudget.Launches counts, int constructions)
    {
        Assert.Equal(constructions, counts.Count("init"));
        Assert.Equal(2 * constructions, counts.Count("config"));
        Assert.Equal(constructions, counts.Count("add"));
        Assert.Equal(constructions, counts.Count("commit"));
    }

    private static string[] SortedConfig(string root) =>
        Git(root, "config", "--local", "--list").Split('\n').Order(StringComparer.Ordinal).ToArray();

    private static string Status(string root) => Git(root, "status", "--porcelain=v1", "--untracked-files=all");

    private static string Git(string root, params string[] arguments) => DirectGit(root, null, arguments);

    private static string DirectGit(string root, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var result = GitCli.RunDirect(root, null, environment, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output.TrimEnd('\r', '\n');
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
