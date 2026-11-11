using System.Reflection;
using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its repository paths and terminates only their reader handles.
// Launch observations use the current test's census window without replacing global observers.
public sealed class HeadLookupReaderRoutingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("head-reader-routing-").FullName;
    private readonly List<string> repositories = [];
    private CliExecutionContext? cliContext;

    public static TheoryData<Type, Func<HeadLookupReaderRoutingTests, string?, string?>> Helpers => new()
    {
        { typeof(ConductorDriver), (_, path) => ConductorDriver.TryResolveGitHead(path!) },
        { typeof(GoalAcceptanceStatusProjector), (_, path) => GoalAcceptanceStatusProjector.TryResolveHead(path!) },
        { typeof(CliCommandHandlers), (fixture, path) => CliCommandHandlers.TryResolveGitHead(fixture.Context(), path) },
        { typeof(ProgressiveReviewGlanceCoordinator), (_, path) => ProgressiveReviewGlanceCoordinator.TryResolveHead(path!) },
        { typeof(ProgressiveReviewSteeringCoordinator), (_, path) => ProgressiveReviewSteeringCoordinator.TryResolveHead(path!) }
    };

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Resolve_CommittedRepository_MatchesRevParse(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method)
    {
        var repo = CreateRepository();
        Assert.Equal(Oracle(repo), Resolve(helper, method, repo));
    }

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Resolve_NonRepository_PreservesPreviousFailureValue(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method)
    {
        var expected = BeforeRouting(helper, root);
        Assert.Equal(helper == typeof(CliCommandHandlers) ? string.Empty : null, expected);
        Assert.Equal(expected, Resolve(helper, method, root));
        if (helper == typeof(CliCommandHandlers))
        {
            Assert.Null(Resolve(helper, method, null));
            Assert.Null(Resolve(helper, method, " \t"));
        }
    }

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Resolve_MissingAndUnbornPaths_PreservesPreviousFailureValue(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method)
    {
        var missing = Path.Combine(root, "missing");
        Assert.Equal(BeforeRouting(helper, missing), Resolve(helper, method, missing));
        var unborn = Directory.CreateDirectory(Path.Combine(root, "unborn")).FullName;
        repositories.Add(unborn);
        Git(unborn, "init", "--initial-branch=fixture-main");
        Assert.Equal(BeforeRouting(helper, unborn), Resolve(helper, method, unborn));
    }

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Resolve_ExternalCommit_ReturnsUpdatedHead(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method)
    {
        var repo = CreateRepository();
        var first = Oracle(repo);
        Assert.Equal(first, Resolve(helper, method, repo));

        // RunWithEnvironment starts a separate git process, outside the persistent reader.
        Git(repo, "commit", "--allow-empty", "-m", "external update");
        var second = Oracle(repo);
        Assert.NotEqual(first, second);
        Assert.Equal(second, Resolve(helper, method, repo));
    }

    [Theory]
    [MemberData(nameof(Helpers))]
    public void Resolve_FiftyCalls_LaunchesNoGitProcess(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method)
    {
        var repo = CreateRepository();
        var expected = Oracle(repo);
        if (helper == typeof(CliCommandHandlers))
            _ = Context();
        Assert.Null(GitRevisionReader.Shared.GetReader(repo));
        var identity = HostCapacitySlotTimingWriter.CurrentTestName();
        Assert.NotEqual("<unknown>", identity);
        HostCapacitySlotGitCensus.EnsureInstalled(nameof(HeadLookupReaderRoutingTests));
        var window = Assert.IsType<HostCapacitySlotGitCensus.Window>(
            HostCapacitySlotGitCensus.Shared.Open(identity));
        HostCapacitySlotGitCensus.GitLaunchDelta? delta;
        try
        {
            for (var call = 0; call < 50; call++)
                Assert.Equal(expected, Resolve(helper, method, repo));
        }
        finally
        {
            delta = window.Close();
        }

        Assert.NotNull(delta);
        Assert.Equal(0, delta.Launches);
        Assert.Empty(delta.Subcommands);
        Assert.Null(GitRevisionReader.Shared.GetReader(repo));
    }

    private string? Resolve(Type helper, Func<HeadLookupReaderRoutingTests, string?, string?> method, string? path)
    {
        return method(this, path);
    }

    private CliExecutionContext Context() => cliContext ??= new CliExecutionContext(
        new AgentOrchestratorKernel(), OrchestratorWorkspace.ForDirectory(root),
        new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents,
        WorkerProfileCatalog.Default(), null);

    private string? BeforeRouting(Type helper, string path)
    {
        if (helper == typeof(CliCommandHandlers))
            return Context().Worktrees.ResolveHead(path);
        var result = GitCli.Run(path, "rev-parse", "HEAD");
        return result.Succeeded ? result.Output.Trim() : null;
    }

    private string CreateRepository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        repositories.Add(repo);
        Git(repo, "init", "--initial-branch=fixture-main");
        Git(repo, "commit", "--allow-empty", "-m", "seed");
        return repo;
    }

    private void Git(string repo, params string[] args)
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
        Assert.True(result.Succeeded, result.Error);
    }

    private static string Oracle(string repo)
    {
        var result = GitCli.Run(repo, "rev-parse", "HEAD");
        Assert.True(result.Succeeded, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
        return result.Output.Trim();
    }

    public void Dispose()
    {
        foreach (var repo in repositories)
            GitRevisionReader.Shared.GetReader(repo)?.Terminate();
        SharedTestSupport.RemoveTempDirectory(root);
    }
}
