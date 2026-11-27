using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyPartialMemberFilterTests
{
    private const string WidgetPart = "src/Fixture/Widget.Core.cs";
    private const string WidgetSibling = "src/Fixture/Widget.Extra.cs";

    [Theory]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    public void ChangedPart_SelectsOnlyReferencesToTheChangedMember(bool bypassCache, int line)
    {
        using var repository = new Repository();
        WriteWidget(repository);

        var baseline = repository.Read([WidgetPart], bypassCache: bypassCache);
        var selection = repository.Read([WidgetPart], ChangedAt(WidgetPart, line), bypassCache);

        Assert.Equal(["AlphaHelperTests", "AlphaTests", "BetaTests"], baseline.TestClassNames);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["AlphaTests"], selection.TestClassNames);
        if (!bypassCache)
        {
            Assert.Equal(ReverseDependencyCacheDisposition.Hit, selection.CacheReceipt!.Disposition);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameTypeSeeds_CombineFiltersAndPreserveWholeTypeMatching(bool wholeTypeSibling)
    {
        using var repository = new Repository();
        WriteWidget(repository);
        var ranges = ChangedAt(WidgetPart, 4);
        if (!wholeTypeSibling)
        {
            ranges.Add(WidgetSibling, [(3, 3)]);
        }

        var selection = repository.Read([WidgetPart, WidgetSibling], ranges);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        string[] expected = wholeTypeSibling ? ["AlphaHelperTests", "AlphaTests", "BetaTests"] : ["AlphaTests", "BetaTests"];
        Assert.Equal(expected, selection.TestClassNames);
    }

    [Fact]
    public void PartialConsumer_UsesItsOwnMembersWithAWholeTypeSeed()
    {
        using var repository = new Repository();
        const string seed = "src/Fixture/Engine.cs";
        repository.Write(seed, "internal class Engine { internal static int Run() => 1; }");
        repository.Write("src/Fixture/Router.Core.cs",
            "internal partial class Router { internal static int Route() => Engine.Run(); }");
        repository.Write("src/Fixture/Router.Extra.cs",
            "internal partial class Router { internal static int Audit() => 0; }");
        repository.WriteTest("RouteTests", "Router.Route()");
        repository.WriteTest("AuditTests", "Router.Audit()");

        var selection = repository.Read([seed], ChangedAt(seed, 1));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["RouteTests"], selection.TestClassNames);
        Assert.Equal([("Router", "RouteTests")], repository.ShippingReach);
    }

    [Theory]
    [InlineData("non-partial")]
    [InlineData("null")]
    [InlineData("empty-map")]
    [InlineData("empty-ranges")]
    [InlineData("wrong-path")]
    [InlineData("no-overlap")]
    [InlineData("type-attribute")]
    [InlineData("unrecorded-field")]
    [InlineData("memberless")]
    public void WholeTypeFallback_MatchesPathOnlySelection(string scenario)
    {
        using var repository = new Repository();
        var declaration = scenario == "non-partial" ? "internal class Widget" : "internal partial class Widget";
        var members = scenario == "memberless" ? "" : "internal static int Alpha() => 1;";
        if (scenario == "non-partial")
        {
            members += "\ninternal static int Beta() => 0;";
        }
        else
        {
            var alpha = scenario == "memberless" ? "internal static int Alpha() => 1;" : "";
            repository.Write(WidgetSibling, $"internal partial class Widget {{ {alpha} internal static int Beta() => 0; }}");
        }

        repository.Write(WidgetPart, $"[System.Obsolete]\n{declaration}\n{{\nprivate int field;\n{members}\n}}");
        repository.WriteTest("AlphaTests", "Widget.Alpha()");
        repository.WriteTest("BetaTests", "Widget.Beta()");
        var ranges = scenario switch
        {
            "null" => null,
            "empty-map" => new Dictionary<string, IReadOnlyList<(int, int)>>(),
            "empty-ranges" => new Dictionary<string, IReadOnlyList<(int, int)>> { [WidgetPart] = [] },
            "wrong-path" => ChangedAt(WidgetSibling, 1),
            "no-overlap" => ChangedAt(WidgetPart, 50),
            "type-attribute" => ChangedAt(WidgetPart, 1),
            "unrecorded-field" or "memberless" => ChangedAt(WidgetPart, 4),
            _ => ChangedAt(WidgetPart, 5)
        };

        var baseline = repository.Read([WidgetPart]);
        var selection = repository.Read([WidgetPart], ranges);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["AlphaTests", "BetaTests"], baseline.TestClassNames);
        Assert.Equal(scenario == "non-partial" ? ["AlphaTests"] : baseline.TestClassNames, selection.TestClassNames);
    }

    [Fact]
    public void ChangedConstant_KeepsDirectAndTransitiveStaticReferences()
    {
        using var repository = new Repository();
        const string limits = "src/Fixture/Limits.Core.cs";
        repository.Write(limits, "internal partial class Limits { internal const int Max = 20; }");
        repository.Write("src/Fixture/Limits.Extra.cs", "internal partial class Limits { internal const int Min = 1; }");
        repository.Write("src/Fixture/Kernel.Core.cs",
            "internal partial class Kernel { internal static int Read() => Limits.Max; }");
        repository.Write("src/Fixture/Kernel.Extra.cs",
            "internal partial class Kernel { internal static int Ignore() => Limits.Min; }");
        repository.WriteTest("AttributeTests", "value", "[Xunit.Theory, Xunit.InlineData(Limits.Max)]", "int value");
        repository.WriteTest("StaticAccessTests", "Limits.Max");
        repository.WriteTest("SiblingConstantTests", "Limits.Min");
        repository.WriteTest("ReadTests", "Kernel.Read()");
        repository.WriteTest("IgnoreTests", "Kernel.Ignore()");

        var baseline = repository.Read([limits]);
        var selection = repository.Read([limits], ChangedAt(limits, 1));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["AttributeTests", "IgnoreTests", "ReadTests", "SiblingConstantTests", "StaticAccessTests"],
            baseline.TestClassNames);
        Assert.Equal(["AttributeTests", "ReadTests", "StaticAccessTests"], selection.TestClassNames);
    }

    private static Dictionary<string, IReadOnlyList<(int StartLine, int EndLine)>> ChangedAt(string path, int line) =>
        new() { [path] = [(line, line)] };

    private static void WriteWidget(Repository repository)
    {
        repository.Write(WidgetPart, """
            internal partial class Widget
            {
                [System.Obsolete]
                internal static int Alpha() => 1;
                internal static int AlphaHelper() => 0;
            }
            """);
        repository.Write(WidgetSibling, """
            internal partial class Widget
            {
                internal static int Beta() => 2;
            }
            """);
        repository.WriteTest("AlphaTests", "Widget.Alpha()");
        repository.WriteTest("BetaTests", "Widget.Beta()");
        repository.WriteTest("AlphaHelperTests", "Widget.AlphaHelper()");
    }

    private sealed class Repository : IDisposable
    {
        private const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-member-filter-{Guid.NewGuid():N}");
        internal List<(string Type, string TestClass)> ShippingReach { get; } = [];

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write($"{TestDirectory}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
        }

        internal ReverseDependencyTestSelection Read(
            string[] changedPaths,
            IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? ranges = null,
            bool bypassCache = true)
        {
            ShippingReach.Clear();
            return ReverseDependencyTestImpactReader.Read(Root, changedPaths,
                bypassCache: bypassCache, changedLineRanges: ranges, propagateShippingSourceReach: true,
                shippingReachDiagnostics: (type, testClass) => ShippingReach.Add((type, testClass)));
        }

        internal void WriteTest(string name, string expression, string attribute = "[Xunit.Fact]", string parameters = "") =>
            Write($"{TestDirectory}/{name}.cs",
                $"public class {name} {{ {attribute} public void Test({parameters}) {{ _ = {expression}; }} }}");

        internal void Write(string path, string source)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, source);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
