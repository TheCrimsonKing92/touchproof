using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyShippingMemberHopTests
{
    private const string Store = "src/Fixture/Store.cs";
    private const string Hub = "src/Fixture/Hub.cs";

    [Fact]
    public void NonPartialConsumer_FollowsOnlyTheReferencingMember()
    {
        using var repository = new Repository();
        WriteStore(repository);
        repository.Write(Hub, """
            internal class Hub
            {
                internal static int Write() => Store.Save();
                internal static int Read() => Store.Load();
            }
            """);
        WriteHubTests(repository);

        var selection = repository.Read(Store, (5, 5));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["WriteTests"], selection.TestClassNames);
        Assert.Equal([("Hub", "WriteTests")], repository.ShippingReach);
    }

    [Fact]
    public void IntraTypeCall_KeepsCallerReach()
    {
        using var repository = new Repository();
        WriteStore(repository);
        repository.Write(Hub, """
            internal class Hub
            {
                internal static int Read() => Forward();
                private static int Forward() => Write();
                internal static int Write() => Store.Save();
                internal static int Ignore() => Store.Load();
            }
            """);
        WriteHubTests(repository);
        repository.WriteTest("IgnoreTests", "Hub.Ignore()");
        repository.WriteTest("ConstructionTests", "new Hub()");

        var selection = repository.Read(Store, (5, 5));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["ConstructionTests", "ReadTests", "WriteTests"], selection.TestClassNames);
    }

    [Fact]
    public void NonPartialKernel_ChangedConstant_KeepsDirectAndTransitiveReach()
    {
        using var repository = new Repository();
        const string limits = "src/Fixture/Limits.cs";
        repository.Write(limits, """
            internal class Limits
            {
                internal const int Max = 20;
                internal const int Min = 1;
            }
            """);
        repository.Write("src/Fixture/Kernel.cs", """
            internal class Kernel
            {
                internal static int Read() => Limits.Max;
                internal static int Ignore() => Limits.Min;
            }
            """);
        repository.WriteTest("AttributeTests", "value", "[Xunit.Theory, Xunit.InlineData(Limits.Max)]", "int value");
        repository.WriteTest("StaticAccessTests", "Limits.Max");
        repository.WriteTest("SiblingConstantTests", "Limits.Min");
        repository.WriteTest("ReadTests", "Kernel.Read()");
        repository.WriteTest("IgnoreTests", "Kernel.Ignore()");

        var baseline = repository.Read(limits);
        var selection = repository.Read(limits, (3, 3));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["AttributeTests", "IgnoreTests", "ReadTests", "SiblingConstantTests", "StaticAccessTests"],
            baseline.TestClassNames);
        Assert.Equal(["AttributeTests", "ReadTests", "StaticAccessTests"], selection.TestClassNames);
        Assert.Equal([("Kernel", "ReadTests")], repository.ShippingReach);
    }

    [Theory]
    [InlineData("field-initializer")]
    [InlineData("constructor")]
    [InlineData("base-list")]
    [InlineData("type-attribute")]
    [InlineData("mixed")]
    public void UnattributedConsumerReference_MatchesWholeTypeSelection(string scenario)
    {
        using var repository = new Repository();
        WriteStore(repository);
        WriteHubTests(repository);
        var source = scenario switch
        {
            "field-initializer" => "internal class Hub { private static int value = Store.Save();",
            "constructor" => "internal class Hub { internal Hub() { _ = Store.Save(); }",
            "base-list" => "internal class Hub : Store {",
            "type-attribute" => "[System.Obsolete(nameof(Store.Save))] internal class Hub {",
            "mixed" => "internal class Hub { private static int value = Store.Save();",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var write = scenario == "mixed" ? "Store.Save()" : "0";
        repository.Write(Hub, $"{source} internal static int Read() => 0; internal static int Write() => {write}; }}");

        var baseline = repository.Read(Store);
        var selection = repository.Read(Store, (5, 5));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["ReadTests", "WriteTests"], baseline.TestClassNames);
        Assert.Equal(baseline.TestClassNames, selection.TestClassNames);
        Assert.Equal([("Hub", "ReadTests"), ("Hub", "WriteTests")], repository.ShippingReach);

        repository.Write(Hub, "internal class Hub { internal static int Read() => 0; internal static int Write() => Store.Save(); }");
        Assert.Equal(["WriteTests"], repository.Read(Store, (5, 5)).TestClassNames);
    }

    [Theory]
    [InlineData(4, 4)] // Field.
    [InlineData(1, 1)] // Type header.
    [InlineData(4, 5)] // Field and member in the same hunk.
    [InlineData(5, 6)] // A member and the gap outside its span.
    public void ChangedRangeOutsideMembers_MatchesWholeTypeSelection(int startLine, int endLine)
    {
        using var repository = new Repository();
        WriteStore(repository);
        repository.WriteTest("SaveTests", "Store.Save()");
        repository.WriteTest("LoadTests", "Store.Load()");

        var baseline = repository.Read(Store);
        var selection = repository.Read(Store, (startLine, endLine));

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["LoadTests", "SaveTests"], baseline.TestClassNames);
        Assert.Equal(baseline.TestClassNames, selection.TestClassNames);
        Assert.Equal(["SaveTests"], repository.Read(Store, (5, 5)).TestClassNames);
    }

    private static void WriteStore(Repository repository) => repository.Write(Store, """
        internal class Store
        {
            // The changed ranges below can cover just the field, just Save, or both.
            private int field;
            internal static int Save() => 1;

            internal static int Load() => 0;
        }
        """);

    private static void WriteHubTests(Repository repository)
    {
        repository.WriteTest("ReadTests", "Hub.Read()");
        repository.WriteTest("WriteTests", "Hub.Write()");
    }

    // Parallel-safe. Each case writes to its own temporary repository and bypasses the shared index cache.
    private sealed class Repository : IDisposable
    {
        private const string TestDirectory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-shipping-member-hop-{Guid.NewGuid():N}");
        internal List<(string Type, string TestClass)> ShippingReach { get; } = [];

        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write($"{TestDirectory}/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
        }

        internal ReverseDependencyTestSelection Read(string path, params (int StartLine, int EndLine)[] ranges)
        {
            ShippingReach.Clear();
            return ReverseDependencyTestImpactReader.Read(Root, [path], bypassCache: true,
                changedLineRanges: ranges.Length == 0 ? null : new Dictionary<string, IReadOnlyList<(int, int)>> { [path] = ranges },
                propagateShippingSourceReach: true,
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
