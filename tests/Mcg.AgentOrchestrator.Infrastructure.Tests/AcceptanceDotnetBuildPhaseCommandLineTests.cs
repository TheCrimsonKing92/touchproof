using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceDotnetBuildPhaseCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-command", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("\t", "\"\t\"")]
    [InlineData("C:\\folder name\\", "\"C:\\folder name\\\\\"")]
    public async Task EnsureAsync_QuotedArgument_PreservesEmptyWhitespaceAndBackslashes(
        string argument, string rendered)
    {
        var phase = new DotnetTestBuildPhase(["dotnet", "build", argument], null);
        var environment = new DotnetBuildEnvironment("owned", _root, _root, Path.Combine(_root, "lock"), [], "fixture");
        var hooks = new AcceptanceDotnetBuildPhaseHooks((_, _, _, _) =>
            Task.FromResult((new AcceptanceCheckResult("build", true, 0, null), false)),
            _ => environment, name => name, () => throw new InvalidOperationException("No cache"),
            (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(null));
        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);
        Assert.Equal("build_command=dotnet build " + rendered, result.Run.Result.ResultSummary);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task EnsureAsync_EachBuildOutcome_RecordsFullCommand(bool cached, bool passed)
    {
        const string shared = "src/Shared.csproj";
        const string first = "tests/First Tests.csproj";
        const string second = "tests/Second.Tests.csproj";
        var artifacts = Path.Combine(_root, "build artifacts");
        var environment = new DotnetBuildEnvironment("owned", _root, artifacts, Path.Combine(_root, "lock"),
            ["--artifacts-path", artifacts, "-maxcpucount:1", "-p:BuildInParallel=false"], "fixture");
        var cache = new DotnetBaseBuildCache(Path.Combine(_root, "cache"));
        var identity = new DotnetBaseBuildCacheIdentity("fixture");
        if (cached)
        {
            foreach (var kind in new[] { "bin", "obj" })
            {
                var directory = Path.Combine(_root, "seed", kind, "Shared", "debug");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "seed.txt"), "seed");
            }
            Assert.True(cache.Publish("main", Path.Combine(_root, "seed"), [shared], identity).Projects.All(p => p.Status == "published"));
        }
        var phase = new DotnetTestBuildPhase(["dotnet", "build", "Example.sln", "-p:RunAnalyzers=false"],
            cached ? new("main", [shared], [first, second], [shared, first, second]) : null);
        var commands = new List<string[]>();
        var hooks = new AcceptanceDotnetBuildPhaseHooks((args, _, prepare, supplied) =>
        {
            Assert.Same(environment, supplied);
            prepare?.Invoke(environment);
            commands.Add(args);
            return Task.FromResult((new AcceptanceCheckResult("build", passed, passed ? 0 : 1,
                null, ResultSummary: "original"), false));
        }, _ => environment, name => name, () => cache, (_, _) => Task.FromResult<DotnetBaseBuildCacheIdentity?>(identity));

        var result = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "attempt", hooks);

        Assert.Equal(passed, result.Run.Result.Passed);
        Assert.Equal(cached && passed ? 2 : 1, commands.Count);
        var summary = result.Run.Result.ResultSummary!;
        var recorded = summary.Split("; ").Where(segment => segment.StartsWith("build_command=")).ToArray();
        Assert.Equal(commands.Select(args => "build_command=" + string.Join(' ',
            args.Concat(environment.Arguments).Select(GoalAcceptanceVerifier.QuoteForDisplay))), recorded);
        Assert.Contains("--artifacts-path \"" + artifacts + "\" -maxcpucount:1 -p:BuildInParallel=false", summary);
        Assert.Contains("original", summary);
        var reused = await AcceptanceDotnetBuildPhase.EnsureAsync(phase, "another-attempt", hooks);
        Assert.False(reused.ContributesToCheck);
        Assert.Equal(summary, reused.Run.Result.ResultSummary);
        Assert.Equal(recorded.Length, commands.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
