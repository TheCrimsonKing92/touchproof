using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns temporary sources and runnable membership; no processes or global state.
public sealed class ImpactReplayChangedSupportStaticImportTests
{
    [Theory]
    [InlineData("ordinary")]
    [InlineData("generic")]
    [InlineData("unresolved-types")]
    public void StaticImportInvocation_SelectsBothNewReferrers(string shape)
    {
        using var sources = new SourceFixture();
        var member = shape switch
        {
            "generic" => "internal static T Append<T>(T value) => value;",
            "unresolved-types" => "internal static object Append(Missing.Store value) => value;",
            _ => "internal static int Append(int value) => value;"
        };
        var call = shape switch
        {
            "generic" => "Append<int>(1)",
            "unresolved-types" => "Append(new Missing.Store())",
            _ => "Append(1)"
        };
        sources.Add("Support.cs", "internal static class Journal { " + member + " }");
        sources.Add("AlphaTests.cs", "using static Journal; namespace SampleTests; public class AlphaTests { void Run() { " + call + "; } }");
        sources.Add("BetaTests.cs", "using static Journal; namespace SampleTests; public class BetaTests { void Run() { " + call + "; } }");
        sources.Add("GammaTests.cs", "namespace SampleTests; public class GammaTests { }");

        var actual = sources.Select();

        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests" }, actual.Classes);
        Assert.Empty(actual.OtherFiles);
    }

    [Fact]
    public void StaticImportChain_SelectsRunnableThroughSecondSupportType()
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public static class Journal { public static int Compute() => 1; }");
        sources.Add("Bridge.cs", "using static Support.Journal; namespace Fixtures; public static class Bridge { public static int Run() => Compute(); }");
        sources.Add("BetaTests.cs", "using static Fixtures.Bridge; namespace SampleTests; public class BetaTests { int value = Run(); }");

        var actual = sources.Select();

        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.BetaTests" }, actual.Classes);
        Assert.Empty(actual.OtherFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameNamedStaticMember_SelectsChangedSourceOnlyWhenItIsACandidate(bool ambiguous)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public static class Journal { public static int Compute() => 1; }");
        sources.Add("Other.cs", "namespace Other; public static class Tools { public static int Compute() => 2; }");
        sources.Add("AlphaTests.cs", "using static Support.Journal; namespace SampleTests; public class AlphaTests { int value = Compute(); }");
        sources.Add("BetaTests.cs", (ambiguous ? "using static Support.Journal; " : "") +
            "using static Other.Tools; namespace SampleTests; public class BetaTests { int value = Compute(); }");
        sources.Add("GammaTests.cs", "using static Support.Journal; namespace SampleTests; public class GammaTests { }");

        var actual = sources.Select();

        Assert.Null(actual.AbandonReason);
        Assert.Equal(ambiguous ? new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests" } :
            new[] { "SampleTests.AlphaTests" }, actual.Classes);
        Assert.Empty(actual.OtherFiles);
    }

    [Theory]
    [InlineData("field-read", "int value = Count;")]
    [InlineData("property-read", "int value = Limit;")]
    [InlineData("const-read", "int value = Max;")]
    [InlineData("field-write", "void Run() { Count = 3; }")]
    [InlineData("property-write", "void Run() { Limit = 4; }")]
    [InlineData("compound-write", "void Run() { Count++; }")]
    [InlineData("event-subscribe", "void Run() { Updated += OnUpdated; } void OnUpdated() { }")]
    [InlineData("event-unsubscribe", "void Run() { Updated -= OnUpdated; } void OnUpdated() { }")]
    public void StaticImportReadAndWrite_SelectsReferrer(string shape, string use)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", """
            namespace Support;
            public static class Journal
            {
                public static int Count;
                public static int Limit { get; set; }
                public const int Max = 5;
                public static event System.Action Updated;
            }
            """);
        sources.Add("BetaTests.cs", "using static Support.Journal; namespace SampleTests; public class BetaTests { " + use + " }");

        var actual = sources.Select();

        Assert.True(actual.AbandonReason is null, shape + ": " + actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.BetaTests" }, actual.Classes);
        Assert.Empty(actual.OtherFiles);
    }

    private sealed class SourceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "impact-static-import-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _sources = [];

        internal void Add(string relativePath, string text)
        {
            var path = Path.GetFullPath(relativePath, _root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            _sources.Add(path);
        }

        internal ImpactReplayChangedSupportSelection Select() =>
            ImpactReplayChangedSupportReferenceScan.Select(_root, ["Support.cs"],
                [new(Path.Combine(_root, "Infrastructure.Tests.csproj"), true, _sources)],
                () => new HashSet<string>(StringComparer.Ordinal)
                {
                    "SampleTests.AlphaTests", "SampleTests.BetaTests", "SampleTests.GammaTests"
                });

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
