using Mcg.AgentOrchestrator.Core;

public sealed class ReverseDependencyChangedLineSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneTypeOverlaps_SelectsOnlyItsConsumer(bool bypassCache)
    {
        using var repository = new Repository();
        var baseline = repository.Read(bypassCache: bypassCache);
        var selected = repository.Read(new Dictionary<string, IReadOnlyList<(int, int)>>
        {
            [Repository.Source] = [(5, 5), (50, 51)]
        }, bypassCache);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, baseline.Outcome);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], baseline.TestClassNames);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selected.Outcome);
        Assert.Equal(["ChangedTypeTests"], selected.TestClassNames);
        if (!bypassCache) Assert.Equal(ReverseDependencyCacheDisposition.Hit, selected.CacheReceipt!.Disposition);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unmatched")]
    [InlineData("all")]
    [InlineData("invalid")]
    public void CompatibilityInputs_MatchPathOnlySelection(string mode)
    {
        using var repository = new Repository();
        var baseline = repository.Read();
        IReadOnlyList<(int, int)> ranges = mode switch
        {
            "all" => [(3, 6), (8, 11)],
            "unmatched" => [(1, 1), (50, 51)],
            "invalid" => [(0, 5), (10, 8)],
            _ => []
        };
        var map = mode == "null" ? null : new Dictionary<string, IReadOnlyList<(int, int)>>
        {
            [mode == "missing" ? "src/Fixture/Other.cs" : Repository.Source] = ranges
        };

        var selection = repository.Read(map);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], baseline.TestClassNames);
        Assert.Equal(baseline.TestClassNames, selection.TestClassNames);
    }

    [Fact]
    public void EquivalentKeys_MergeRangesUsingExistingPathIdentity()
    {
        using var repository = new Repository();
        var one = repository.Read(new Dictionary<string, IReadOnlyList<(int, int)>>
        {
            [" /SRC\\Fixture\\Types.cs "] = [(5, 5)]
        });
        var both = repository.Read(new Dictionary<string, IReadOnlyList<(int, int)>>
        {
            [Repository.Source] = [(5, 5)],
            [" /SRC\\Fixture\\Types.cs "] = [(10, 10)]
        });

        Assert.Equal(["ChangedTypeTests"], one.TestClassNames);
        Assert.Equal(["ChangedTypeTests", "UntouchedTypeTests"], both.TestClassNames);
    }

    private sealed class Repository : IDisposable
    {
        internal const string Source = "src/Fixture/Types.cs";
        private string Root { get; } = Path.Combine(Path.GetTempPath(), $"mcg-line-seed-{Guid.NewGuid():N}");
        internal Repository()
        {
            Write("src/Fixture/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Fixture/Fixture.csproj\" /></ItemGroup></Project>");
            Write(Source, "using System;\n\ninternal class ChangedType\n{\n    public int Value => 1;\n}\n\ninternal class UntouchedType\n{\n    public int Value => 2;\n}\n");
            foreach (var name in new[] { "ChangedType", "UntouchedType" })
                Write($"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/{name}Tests.cs",
                    $"public class {name}Tests {{ private {name} value; [Xunit.Fact] public void Works() {{ }} }}");
        }
        internal ReverseDependencyTestSelection Read(
            IReadOnlyDictionary<string, IReadOnlyList<(int StartLine, int EndLine)>>? ranges = null,
            bool bypassCache = true) => ReverseDependencyTestImpactReader.Read(Root, [Source],
                bypassCache: bypassCache, changedLineRanges: ranges);
        private void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
