using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory and census sidecar; no global observer
// or environment is replaced, and the fixture delegates are never mutated.
public sealed class HostCapacitySlotGitCallerStaticDelegateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("slot-git-static-delegates-").FullName;

    [Fact]
    public async Task Release_StaticInitializerLambda_UsesInvokingMethod()
    {
        var path = Path.Combine(root, "static.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
        {
            Succeeded(StaticRunnerFixture.Launch(root));
            Succeeded(StaticRunnerFixture.Launch(root));
        }

        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(2, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.DoesNotContain(callers.Keys, key => key.Contains(".cctor", StringComparison.Ordinal));
        Assert.Equal(2, callers[Key("StaticRunnerFixture.Launch")]);
        Assert.Equal(0, callers["other"]);
        Assert.Equal(2, callers.Count);
    }

    [Fact]
    public async Task Release_InstanceInitializerLambda_UsesInvokingMethod()
    {
        var fixture = new InstanceRunnerFixture();
        var path = Path.Combine(root, "instance.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
            Succeeded(fixture.Launch(root));

        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(1, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.DoesNotContain(callers.Keys, key => key.Contains(".ctor", StringComparison.Ordinal));
        Assert.Equal(1, callers[Key("InstanceRunnerFixture.Launch")]);
        Assert.Equal(0, callers["other"]);
        Assert.Equal(2, callers.Count);
    }

    [Fact]
    public async Task Release_DirectLaunch_KeepsCallingMethod()
    {
        var path = Path.Combine(root, "direct.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
            Succeeded(LaunchDirectly());

        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(1, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.Equal(1, callers[Key(nameof(LaunchDirectly))]);
        Assert.Equal(0, callers["other"]);
        Assert.Equal(2, callers.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private GitCli.GitResult LaunchDirectly() => GitCli.Run(root, "--version");

    private static string Key(string method) => $"test:HostCapacitySlotGitCallerStaticDelegateTests.{method}|--version";
    private static void Succeeded(GitCli.GitResult result) => Assert.True(result.Succeeded, result.Error);
    private static JsonDocument ReadRow(string path) => JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
    private static IReadOnlyDictionary<string, int> Callers(JsonDocument row) => HostCapacitySlotGitCensus.GroupByCaller(
        row.RootElement.GetProperty("git_callers").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32()));

    private static class StaticRunnerFixture
    {
        internal static Func<string, string[], GitCli.GitResult> Runner { get; } =
            [MethodImpl(MethodImplOptions.NoInlining)] (directory, args) => GitCli.Run(directory, args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static GitCli.GitResult Launch(string directory) => Runner(directory, ["--version"]);
    }

    private sealed class InstanceRunnerFixture
    {
        private readonly Func<string, string[], GitCli.GitResult> runner =
            [MethodImpl(MethodImplOptions.NoInlining)] (directory, args) => GitCli.Run(directory, args);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal GitCli.GitResult Launch(string directory) => runner(directory, ["--version"]);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
