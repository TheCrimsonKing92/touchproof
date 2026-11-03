using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owned repositories/sidecars and local census identities. The disabled
// path uses a local observer target, never changing the gate's global environment.
public sealed class HostCapacitySlotGitCallerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("slot-git-callers-").FullName;

    [Fact]
    public async Task Release_ProductAndHelperLaunches_RecordsSourceCallers()
    {
        var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        var environment = GitEnvironment();
        Succeeded(GitCli.RunWithEnvironment(repo, environment, "init"));
        Succeeded(GitCli.RunWithEnvironment(repo, environment, "commit", "--allow-empty", "-m", "seed"));
        // Git accepts this HEAD spelling; the conservative in-process check defers to the probe.
        var headPath = Path.Combine(repo, ".git", "HEAD");
        File.WriteAllText(headPath, File.ReadAllText(headPath).Replace("ref: ", "ref:", StringComparison.Ordinal));
        var path = Path.Combine(root, "fixture.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
        {
            var requireGitWorkTree = typeof(GoalWorktrees).GetMethod("RequireGitWorkTree",
                BindingFlags.Static | BindingFlags.NonPublic, [typeof(string)])!.CreateDelegate<Action<string>>();
            requireGitWorkTree(repo);
            requireGitWorkTree(repo);
            CommitThroughHelper(repo, environment);
        }

        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(3, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.Equal(2, row.RootElement.GetProperty("git_subcommands").GetProperty("rev-parse").GetInt32());
        Assert.Equal(1, row.RootElement.GetProperty("git_subcommands").GetProperty("commit").GetInt32());
        Assert.Equal(3, callers.Count);
        Assert.Equal(2, callers["src:Mcg.AgentOrchestrator.Infrastructure.GoalWorktrees.RequireGitWorkTree|rev-parse"]);
        Assert.Equal(1, callers[Key(nameof(CommitThroughHelper), "commit")]);
        Assert.Equal(0, callers["other"]);
        Assert.Equal(1, budget.AvailableSlots);
    }

    [Fact]
    public async Task Release_GeneratedFrames_UsesEnclosingSourceMethods()
    {
        var path = Path.Combine(root, "generated.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
        {
            await LaunchAsync();
            Action launch = [MethodImpl(MethodImplOptions.NoInlining)] () => Succeeded(GitCli.Run(root, "--version"));
            launch();
            await LocalAsync();
        }
        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(3, row.RootElement.GetProperty("git_launches").GetInt32());
        Assert.Equal(1, callers[Key(nameof(LaunchAsync), "--version")]);
        Assert.Equal(2, callers[Key(nameof(Release_GeneratedFrames_UsesEnclosingSourceMethods), "--version")]);
        Assert.Equal(0, callers["other"]);
        Assert.Equal(3, callers.Count);
        Assert.All(callers.Keys, key => { Assert.DoesNotContain('<', key); Assert.DoesNotContain('>', key); });

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task LocalAsync()
        {
            await Task.Yield();
            Func<Task> launch = async () =>
            {
                await Task.Yield();
                Succeeded(GitCli.Run(root, "--version"));
            };
            await launch();
        }
    }

    [Fact]
    public async Task Release_ProbeLaunch_SkipsProbeInfrastructureFrames()
    {
        var path = Path.Combine(root, "probe.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync()) ProbeThroughHelper();
        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(2, callers.Count);
        Assert.Equal(1, callers[Key(nameof(ProbeThroughHelper), "--version")]);
        Assert.Equal(0, callers["other"]);
    }

    [Fact]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary()
    {
        var census = new HostCapacitySlotGitCensus(() => "forwarding");
        var window = census.Open("forwarding")!;
        // Exercise the actual post-start notification boundary with a local target,
        // without replacing the observer used by concurrently running git launches.
        var notify = typeof(GitCli).GetMethod("NotifyLaunch", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Action<Action<string>, string[]>>();
        notify(ForwardingObserver(census), ["rev-parse"]);
        var delta = window.Close()!;
        Assert.Equal(1, delta.Launches);
        AssertSingleCaller(delta.Callers,
            Key(nameof(Record_ForwardingObserver_UsesCallerOutsideLaunchBoundary), "rev-parse"), 1);
    }

    [Fact]
    public async Task Record_NoEligibleFrame_UsesUnknownCaller()
    {
        var census = new HostCapacitySlotGitCensus(() => "unknown");
        var window = census.Open("unknown")!;
        // A compiled expression and the task scheduler supply no source-type frame.
        // Await completion before inspecting the counts, without a test lambda in the walk.
        var record = Expression.Lambda<Action>(Expression.Call(Expression.Constant(census),
            nameof(HostCapacitySlotGitCensus.Record), Type.EmptyTypes, Expression.Constant("rev-parse"))).Compile();
        await Task.Run(record);
        var delta = window.Close()!;
        Assert.Equal(1, delta.Launches);
        Assert.Equal(KeyValuePair.Create("test:<unknown>|rev-parse", 1), Assert.Single(delta.Callers!));
    }

    [Fact]
    public async Task Release_NestedGenericType_PreservesOrdinaryMoveNextMethod()
    {
        var path = Path.Combine(root, "nested.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync()) GenericCaller<int>.MoveNext(root);
        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(2, callers.Count);
        Assert.Equal(1, callers["test:HostCapacitySlotGitCallerTests.GenericCaller.MoveNext|--version"]);
        Assert.Equal(0, callers["other"]);
    }

    [Fact]
    public async Task Release_MoreThanTwentyCallers_KeepsLargestAndOrdinalTies()
    {
        var path = Path.Combine(root, "cap.jsonl");
        var budget = new HostCapacityTestBudget(1, _ => path);
        await using (var held = await budget.EnterAsync())
            for (var index = 1; index <= 22; index++)
                for (var count = 0; count < index; count++)
                    HostCapacitySlotGitCensus.Shared.Record("rev-parse", $"test:Caller{index:D2}.Launch");
        using var row = ReadRow(path);
        var callers = Callers(row);
        Assert.Equal(21, callers.Count);
        Assert.Equal(3, callers["other"]);
        Assert.Equal(253, callers.Values.Sum());
        Assert.Equal(253, row.RootElement.GetProperty("git_launches").GetInt32());
        for (var index = 3; index <= 22; index++)
            Assert.Equal(index, callers[$"test:Caller{index:D2}.Launch|rev-parse"]);

        var ties = Path.Combine(root, "ties.jsonl");
        await using (var held = await new HostCapacityTestBudget(1, _ => ties).EnterAsync())
            for (var index = 21; index >= 1; index--)
                HostCapacitySlotGitCensus.Shared.Record("commit", $"test:Caller{index:D2}.Launch");
        using var tiedRow = ReadRow(ties);
        var tied = Callers(tiedRow);
        Assert.Equal(21, tied.Count);
        Assert.Equal(1, tied["other"]);
        Assert.Equal(21, tied.Values.Sum());
        for (var index = 1; index <= 20; index++)
            Assert.Equal(1, tied[$"test:Caller{index:D2}.Launch|commit"]);
    }

    [Fact]
    public void Install_UnsetTimingDestination_DoesNoStackWalk()
    {
        // Same destination lookup seam as HostCapacityTestBudget; no ambient mutation.
        Func<string, string?> unsetEnvironment = _ => null;
        Action<string>? observer = null;
        var before = HostCapacitySlotGitCensus.StackWalksOnCurrentThread;
        Assert.False(HostCapacitySlotGitCensus.EnsureInstalled(
            unsetEnvironment(HostCapacitySlotTimingWriter.VariableName), ref observer));
        Assert.False(HostCapacitySlotGitCensus.EnsureInstalled("", ref observer));
        observer?.Invoke("rev-parse");
        Assert.Null(observer);
        Assert.Equal(before, HostCapacitySlotGitCensus.StackWalksOnCurrentThread);
        Assert.True(HostCapacitySlotGitCensus.EnsureInstalled("enabled", ref observer));
        Assert.NotNull(observer);
        observer("rev-parse");
        Assert.Equal(before + 1, HostCapacitySlotGitCensus.StackWalksOnCurrentThread);
    }

    [Fact]
    public async Task Record_Attribution_ReportsAddedMicrosecondsPerLaunch()
    {
        var census = new HostCapacitySlotGitCensus(() => "measurement");
        var caller = "test:HostCapacitySlotGitCallerTests.Record_Attribution_ReportsAddedMicrosecondsPerLaunch";
        for (var index = 0; index < 100; index++) { census.Record("rev-parse"); census.Record("rev-parse", caller); }
        const int samples = 5000;
        var before = HostCapacitySlotGitCensus.StackWalksOnCurrentThread;
        var attributed = Measure(() => census.Record("rev-parse"), samples);
        var after = HostCapacitySlotGitCensus.StackWalksOnCurrentThread;
        var aggregation = Measure(() => census.Record("rev-parse", caller), samples);
        Assert.Equal(samples, after - before);
        Assert.Equal(after, HostCapacitySlotGitCensus.StackWalksOnCurrentThread);
        output.WriteLine($"Caller attribution added time per launch: {(attributed - aggregation).TotalMicroseconds / samples:F3} us; " +
            $"samples={samples}; stack walks per launch={(after - before) / samples}; " +
            $"with={attributed.TotalMicroseconds / samples:F3} us; without={aggregation.TotalMicroseconds / samples:F3} us");

        await MeasureAsync(census, true, 100);
        await MeasureAsync(census, false, 100);
        var asyncAttributed = await MeasureAsync(census, true, samples);
        var asyncAggregation = await MeasureAsync(census, false, samples);
        Assert.Equal(samples, asyncAttributed.Walks);
        Assert.Equal(0, asyncAggregation.Walks);
        output.WriteLine($"Async caller attribution added time per launch: " +
            $"{(asyncAttributed.Elapsed - asyncAggregation.Elapsed).TotalMicroseconds / samples:F3} us; samples={samples}; " +
            $"with={asyncAttributed.Elapsed.TotalMicroseconds / samples:F3} us; " +
            $"without={asyncAggregation.Elapsed.TotalMicroseconds / samples:F3} us");
    }

    [Fact]
    public void ProductAssemblies_SourceProjects_MatchOriginClassification()
    {
        var repo = VerifiedRepositoryRoot.Find();
        var assemblies = Directory.EnumerateDirectories(Path.Combine(repo, "src"))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        Assert.Equal(assemblies, HostCapacitySlotGitCensus.ProductAssemblyNames.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(HostCapacitySlotGitCallerTests).Assembly.GetName().Name!,
            HostCapacitySlotGitCensus.ProductAssemblyNames);
    }

    [Fact]
    public void Windows_OverlappingHolds_KeepIndependentCallerDeltas()
    {
        var census = new HostCapacitySlotGitCensus(() => "same");
        var first = census.Open("same")!;
        census.Record("commit", "test:A.First");
        var second = census.Open("same")!;
        census.Record("commit", "test:B.Second");
        Assert.Equal(2, first.Close()!.Callers!.Values.Sum());
        census.Record("commit", "test:B.Second");
        var delta = second.Close()!;
        Assert.Equal(2, delta.Launches);
        Assert.Equal(KeyValuePair.Create("test:B.Second|commit", 2), Assert.Single(delta.Callers!));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CommitThroughHelper(string repo, IReadOnlyDictionary<string, string> environment) =>
        Succeeded(GitCli.RunWithEnvironment(repo, environment, "commit", "--allow-empty", "-m", "helper"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task LaunchAsync()
    {
        await Task.Yield();
        Succeeded(GitCli.Run(root, "--version"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ProbeThroughHelper()
    {
        var result = InfrastructureTestSupport.RunGitProbe(root, ["--version"]);
        Assert.True(result.Succeeded, result.StandardError);
    }

    private Dictionary<string, string> GitEnvironment() => new()
    {
        ["GIT_AUTHOR_NAME"] = "Caller", ["GIT_AUTHOR_EMAIL"] = "caller@example.test",
        ["GIT_COMMITTER_NAME"] = "Caller", ["GIT_COMMITTER_EMAIL"] = "caller@example.test",
        ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
        ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
    };

    private static TimeSpan Measure(Action action, int samples)
    {
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < samples; index++) action();
        return Stopwatch.GetElapsedTime(started);
    }

    private static async Task<(TimeSpan Elapsed, long Walks)> MeasureAsync(
        HostCapacitySlotGitCensus census, bool attribute, int samples)
    {
        await Task.Yield();
        var caller = "test:HostCapacitySlotGitCallerTests.MeasureAsync";
        var before = HostCapacitySlotGitCensus.StackWalksOnCurrentThread;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < samples; index++)
        {
            if (attribute) census.Record("rev-parse");
            else census.Record("rev-parse", caller);
        }
        return (Stopwatch.GetElapsedTime(started), HostCapacitySlotGitCensus.StackWalksOnCurrentThread - before);
    }

    private static Action<string> ForwardingObserver(HostCapacitySlotGitCensus census) =>
        [MethodImpl(MethodImplOptions.NoInlining)] (command) => census.Record(command);

    private static string Key(string method, string command) => $"test:HostCapacitySlotGitCallerTests.{method}|{command}";

    internal static void AssertSingleCaller(
        IReadOnlyDictionary<string, int>? callers, string expectedKey, int expectedCount)
    {
        var matches = callers is { Count: 1 } &&
            HostCapacitySlotGitCensus.GroupByCaller(callers).Single().Equals(KeyValuePair.Create(expectedKey, expectedCount));
        var fullKeys = callers is null ? "<null>" :
            string.Join(Environment.NewLine, callers.Select(entry => $"{entry.Key} = {entry.Value}"));
        Assert.True(matches,
            $"Expected single caller {expectedKey} = {expectedCount}.{Environment.NewLine}Full keys:{Environment.NewLine}{fullKeys}");
    }

    private static void Succeeded(GitCli.GitResult result) => Assert.True(result.Succeeded, result.Error);
    private static JsonDocument ReadRow(string path) => JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
    private static IReadOnlyDictionary<string, int> Callers(JsonDocument row) => HostCapacitySlotGitCensus.GroupByCaller(
        row.RootElement.GetProperty("git_callers").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetInt32()));

    private sealed class GenericCaller<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void MoveNext(string directory) => Succeeded(GitCli.Run(directory, "--version"));
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
