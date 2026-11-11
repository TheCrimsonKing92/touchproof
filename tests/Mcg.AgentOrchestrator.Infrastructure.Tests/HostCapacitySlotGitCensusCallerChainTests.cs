using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: local observers/censuses, plus an owned sidecar for the budget case.
// NotifyLaunch exercises the real boundary without starting git or replacing its observer.
public sealed class HostCapacitySlotGitCensusCallerChainTests
{
    private const string Prefix = "test:HostCapacitySlotGitCensusCallerChainTests.";
    private static readonly Action<Action<string>, string[]> Notify = GitCli.NotifyLaunch;

    [Theory]
    [InlineData("noncapturing")]
    [InlineData("local")]
    [InlineData("this")]
    public void Record_ConstructorLambda_UsesLaterInvoker(string capture)
    {
        var census = new HostCapacitySlotGitCensus(() => "constructor");
        var fixture = new ConstructorFixture(capture, census.Record);
        var window = census.Open("constructor")!;

        fixture.Launch(census.Record);

        var key = Assert.Single(window.Close()!.Callers!).Key;
        Assert.Equal(Prefix + "ConstructorFixture.Launch", HostCapacitySlotGitCensus.CallerOf(key));
        Assert.DoesNotContain(".ctor", key);
    }

    [Fact]
    public void Record_HelperWithTwoInvokers_KeepsOneParentPerKey()
    {
        var census = new HostCapacitySlotGitCensus(() => "parents");
        var window = census.Open("parents")!;

        ParentA(census.Record);
        ParentB(census.Record);

        var callers = window.Close()!.Callers!;
        Assert.Equal(2, callers.Count);
        Assert.Equal(1, callers[Prefix + "Helper<-HostCapacitySlotGitCensusCallerChainTests.ParentA|rev-list"]);
        Assert.Equal(1, callers[Prefix + "Helper<-HostCapacitySlotGitCensusCallerChainTests.ParentB|rev-list"]);
        Assert.All(callers.Keys, key =>
        {
            Assert.Equal(Prefix + "Helper", HostCapacitySlotGitCensus.CallerOf(key));
            Assert.DoesNotContain(':', HostCapacitySlotGitCensus.ParentOf(key)!);
            Assert.DoesNotContain("<-", HostCapacitySlotGitCensus.ParentOf(key)!);
        });
    }

    [Fact]
    public async Task Record_NoResolvableParent_KeepsLegacyKey()
    {
        var census = new HostCapacitySlotGitCensus(() => "no-parent");
        var window = census.Open("no-parent")!;
        // The compiled expression has no declaring source type; the scheduler supplies
        // no Mcg parent. There must be no test lambda on the synchronous launch stack.
        var launch = Expression.Lambda<Action>(Expression.Call(
            typeof(HostCapacitySlotGitCensusCallerChainTests).GetMethod(nameof(Helper),
                BindingFlags.NonPublic | BindingFlags.Static)!, Expression.Constant((Action<string>)census.Record),
            Expression.Constant("rev-parse"))).Compile();

        try { await Task.Run(launch).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Hang waiting for the parentless launch to finish.", error);
        }

        var delta = window.Close()!;
        Assert.Equal(1, delta.Launches);
        Assert.Equal(KeyValuePair.Create(Prefix + "Helper|rev-parse", 1), Assert.Single(delta.Callers!));
    }

    [Fact]
    public void Record_StaticConstructorLambda_StillUsesInvoker()
    {
        var census = new HostCapacitySlotGitCensus(() => "static");
        var window = census.Open("static")!;

        StaticFixture.Launch(census.Record);

        var key = Assert.Single(window.Close()!.Callers!).Key;
        Assert.Equal(Prefix + "StaticFixture.Launch", HostCapacitySlotGitCensus.CallerOf(key));
        Assert.DoesNotContain(".cctor", key);
    }

    [Fact]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Record_SameMemberOverloads_SkipsDuplicateParent()
    {
        var census = new HostCapacitySlotGitCensus(() => "overload");
        var window = census.Open("overload")!;

        Overload(census.Record);

        var key = Assert.Single(window.Close()!.Callers!).Key;
        Assert.Equal(Prefix + "Overload", HostCapacitySlotGitCensus.CallerOf(key));
        Assert.Equal("HostCapacitySlotGitCensusCallerChainTests." +
            nameof(Record_SameMemberOverloads_SkipsDuplicateParent), HostCapacitySlotGitCensus.ParentOf(key));
    }

    [Fact]
    public void Window_MixedLaunches_PreservesLegacyCounts()
    {
        var census = new HostCapacitySlotGitCensus(() => "totals");
        var fixture = new ConstructorFixture("local", census.Record);
        ParentA(census.Record); // Pre-hold launch must not leak into the delta.
        var window = census.Open("totals")!;

        ParentA(census.Record);
        ParentA(census.Record);
        ParentB(census.Record);
        fixture.Launch(census.Record);
        StaticFixture.Launch(census.Record);

        var delta = window.Close()!;
        Assert.Equal(5, delta.Launches);
        Assert.Equal(2, delta.Subcommands.Count);
        Assert.Equal(3, delta.Subcommands["rev-list"]);
        Assert.Equal(2, delta.Subcommands["rev-parse"]);
        Assert.Equal(delta.Launches, delta.Callers!.Values.Sum());
        var legacy = HostCapacitySlotGitCensus.GroupByCaller(delta.Callers);
        Assert.Equal(3, legacy.Count);
        Assert.Equal(3, legacy[Prefix + "Helper|rev-list"]);
        Assert.Equal(1, legacy[Prefix + "ConstructorFixture.Launch|rev-parse"]);
        Assert.Equal(1, legacy[Prefix + "StaticFixture.Launch|rev-parse"]);
    }

    [Fact]
    public async Task Release_EnrichedCallers_PreservesSidecarTotalsIncludingOther()
    {
        var root = Directory.CreateTempSubdirectory("slot-git-chain-").FullName;
        try
        {
            var path = Path.Combine(root, "census.jsonl");
            var budget = new HostCapacityTestBudget(1, _ => path);
            await using (var held = await budget.EnterAsync())
            {
                ParentA(HostCapacitySlotGitCensus.Shared.Record);
                ParentB(HostCapacitySlotGitCensus.Shared.Record);
                for (var index = 0; index < HostCapacitySlotTimingWriter.GitCallerLimit + 1; index++)
                    HostCapacitySlotGitCensus.Shared.Record("rev-parse", $"test:Helper<-Parent{index}");
            }

            using var row = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            var launches = row.RootElement.GetProperty("git_launches").GetInt32();
            var subcommands = row.RootElement.GetProperty("git_subcommands");
            var callers = row.RootElement.GetProperty("git_callers").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetInt32());
            Assert.Equal(HostCapacitySlotTimingWriter.GitCallerLimit + 3, launches);
            Assert.Equal(2, subcommands.EnumerateObject().Count());
            Assert.Equal(2, subcommands.GetProperty("rev-list").GetInt32());
            Assert.Equal(HostCapacitySlotTimingWriter.GitCallerLimit + 1,
                subcommands.GetProperty("rev-parse").GetInt32());
            Assert.Equal(launches, callers.Values.Sum());
            Assert.Equal(3, callers["other"]);
            Assert.Equal(HostCapacitySlotTimingWriter.GitCallerLimit + 1, callers.Count);
            Assert.All(callers.Keys.Where(key => key != "other"), key => Assert.Contains("<-", key));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void GroupByCaller_MixedFormats_GroupsCallerUnlessParentRequested()
    {
        var sources = new Dictionary<string, int>
        {
            ["src:Helper|rev-list"] = 1,
            ["src:Helper<-ParentA|rev-list"] = 2,
            ["src:Helper<-ParentB|rev-list"] = 3,
            ["test:<unknown>|custom|<-argument"] = 4,
            ["other"] = 5
        };

        var grouped = HostCapacitySlotGitCensus.GroupByCaller(sources);

        Assert.Equal(3, grouped.Count);
        Assert.Equal(6, grouped["src:Helper|rev-list"]);
        Assert.Equal(4, grouped["test:<unknown>|custom|<-argument"]);
        Assert.Equal(5, grouped["other"]);
        var withParents = HostCapacitySlotGitCensus.GroupByCaller(sources, includeParent: true);
        Assert.Equal(sources.OrderBy(pair => pair.Key), withParents.OrderBy(pair => pair.Key));
        Assert.Null(HostCapacitySlotGitCensus.ParentOf("src:Helper|rev-list"));
        Assert.Null(HostCapacitySlotGitCensus.ParentOf("test:<unknown>|custom|<-argument"));
        Assert.Equal("test:<unknown>", HostCapacitySlotGitCensus.CallerOf("test:<unknown>|custom|<-argument"));
        Assert.Equal("ParentA", HostCapacitySlotGitCensus.ParentOf("src:Helper<-ParentA|rev-list"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Helper(Action<string> observer, string command) => Notify(observer, [command]);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ParentA(Action<string> observer) => Helper(observer, "rev-list");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ParentB(Action<string> observer) => Helper(observer, "rev-list");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Overload(Action<string> observer) => Overload(observer, "rev-parse");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Overload(Action<string> observer, string command) => Notify(observer, [command]);

    private sealed class ConstructorFixture
    {
        private readonly Action<string> storedObserver;
        private readonly Action<Action<string>> runner;

        internal ConstructorFixture(string capture, Action<string> observer)
        {
            storedObserver = observer;
            var localObserver = observer;
            runner = capture switch
            {
                "noncapturing" => [MethodImpl(MethodImplOptions.NoInlining)] (target) => Notify(target, ["rev-parse"]),
                "local" => [MethodImpl(MethodImplOptions.NoInlining)] (_) => Notify(localObserver, ["rev-parse"]),
                "this" => [MethodImpl(MethodImplOptions.NoInlining)] (_) => Notify(storedObserver, ["rev-parse"]),
                _ => throw new ArgumentOutOfRangeException(nameof(capture))
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Launch(Action<string> observer) => runner(observer);
    }

    private static class StaticFixture
    {
        private static readonly Action<Action<string>> Runner =
            [MethodImpl(MethodImplOptions.NoInlining)] (observer) => Notify(observer, ["rev-parse"]);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Launch(Action<string> observer) => Runner(observer);
    }
}
