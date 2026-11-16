using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: in-memory sources and counters scoped to the current execution flow.
public sealed class AcceptanceTestClassSourceScannerParseCountTests
{
    private static readonly string[] Sources =
    [
        """
        namespace Fixture;
        public class DerivedFactAttribute : Xunit.FactAttribute { }
        public static class Collections { public const string Name = "fixture"; }
        """,
        """
        namespace Fixture;
        [Xunit.Collection(Collections.Name)]
        public class DerivedTests { [DerivedFact] public void Runs() { } }
        public class PlainTests { [Xunit.Fact] public void Runs() { } }
        """
    ];

    [Fact]
    public void SharedTrees_PreserveDescriptorsWithoutParsingAgain()
    {
        var trees = Sources.Select(source => CSharpSyntaxTree.ParseText(source)).ToArray();
        IReadOnlyList<AcceptanceTestClassDescriptor> expected;
        using (var textParses = AcceptanceTestClassSourceScanner.CountParsesForTests())
        {
            expected = AcceptanceTestClassSourceScanner.ScanSourceTexts(Sources);
            Assert.Equal(Sources.Length, textParses.Count);
        }
        using var treeParses = AcceptanceTestClassSourceScanner.CountParsesForTests();
        var actual = AcceptanceTestClassSourceScanner.ScanSyntaxTreesForTests(trees);
        Assert.Equal(expected, actual);
        Assert.Equal(new[]
        {
            new AcceptanceTestClassDescriptor("Fixture.DerivedTests", "fixture"),
            new AcceptanceTestClassDescriptor("Fixture.PlainTests", null)
        }, actual);
        Assert.Equal(0, treeParses.Count);
        Assert.Empty(AcceptanceTestClassSourceScanner.ScanSyntaxTreesForTests([]));
    }

    [Fact]
    public void ParseCounter_CountsParallelExtractsAndRestoresOuterScope()
    {
        using var outer = AcceptanceTestClassSourceScanner.CountParsesForTests();
        AcceptanceTestClassSourceScanner.ScanSourceTexts(Sources);
        using (var inner = AcceptanceTestClassSourceScanner.CountParsesForTests())
        {
            Parallel.For(0, Sources.Length, index => AcceptanceTestClassSourceScanner.ExtractText(Sources[index]));
            Assert.Equal(Sources.Length, inner.Count);
            Assert.Equal(Sources.Length, outer.Count);
        }
        AcceptanceTestClassSourceScanner.ScanSourceTexts(Sources);
        Assert.Equal(2 * Sources.Length, outer.Count);
    }
}
