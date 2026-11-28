using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OnDiskParsedRootRemainingSitesRoutingTests : IDisposable
{
    private const string LaneProject = "tests/Lane/Lane.Tests.csproj";
    private const string OtherProject = "tests/Other.Tests/Other.Tests.csproj";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private readonly string _marker = "\n// fixture " + Guid.NewGuid().ToString("N");

    [Fact]
    public void ScanSources_UnchangedThenRewritten_ReusesAndRefreshesDeclarations()
    {
        const string file = "tests/Lane/Tests.cs";
        const string before = "class Before { [Xunit.Fact] public void Runs() {} }";
        const string after = "class AfterWithLongerName { [Xunit.Fact] public void Runs() {} }";
        WriteLaneManifest();
        var path = Write(file, before + _marker);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);

        AssertReload(parses, path, after + _marker, () =>
        {
            var source = Assert.Single(AcceptanceTestClassSourceScanner.ScanSources(_root));
            return new[] { (source.FullName, Assert.Single(source.SourcePaths), source.SourceText) };
        }, [("Before", file, before + _marker)], [("AfterWithLongerName", file, after + _marker)]);
    }

    [Fact]
    public void ScanSources_MalformedSource_ReusesRootAndPreservesParseError()
    {
        WriteLaneManifest();
        var path = Write("tests/Lane/Malformed.cs",
            "class Broken { [Xunit.Fact] public void Runs() {}" + _marker);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestClassSourceScanner.ScanSources(_root));
            Assert.Equal($"Cannot parse test source '{path}'.", error.Message);
            Assert.Equal(1L, parses.CountFor(path));
        }
    }

    [Fact]
    public void ReplayCollectionRetry_UnchangedThenRewritten_ReusesRootAndRefreshesExclusions()
    {
        WriteLaneManifest();
        Write("tests/Lane/Lane.cs", "class LaneTests { [Xunit.Fact] public void Runs() {} }");
        Write(OtherProject, "<Project />");
        var path = Write("tests/Other.Tests/Tests.cs", Source("Bad"));
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);

        AssertReload(parses, path, Source("BadRenamedLonger"), () =>
        {
            var original = File.ReadAllText(path);
            var result = ImpactReplayChangedTestSupportSelection.RunnableClasses(
                _root, new ImpactReplayChangedSourceRouting(_root));
            Assert.Equal(["Good", "LaneTests"], result.Runnable.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(original, File.ReadAllText(path));
            return result.Excluded.ToArray();
        }, ["Bad"], ["BadRenamedLonger"]);

        string Source(string excludedClass) => $$"""
            [Xunit.Collection(Missing.Name)]
            public class {{excludedClass}} { [Xunit.Fact] public void Runs() {} }
            public class Good : {{excludedClass}} {}
            """ + _marker;
    }

    [Fact]
    public void BareSelector_UnchangedThenRewritten_RefreshesMethodOwnerAndSkipsUnrelatedFile()
    {
        const string selector = "Runs";
        var unrelated = Write("tests/Other.Tests/Unrelated.cs", "class Unrelated {}" + _marker);
        var path = Write("tests/Other.Tests/Owner.cs", "class OwnerA { [Xunit.Fact] public void Runs() {} }" + _marker);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);

        AssertReload(parses, path, "class OwnerBLonger { [Xunit.Fact] public void Runs() {} }" + _marker, () =>
        {
            Assert.True(FocusedEvidenceBareSelectorResolution.TryResolve(
                () => [unrelated, path], OtherProject, selector, selector, out var selection, out _));
            Assert.Equal(GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Method, selection.Kind);
            return new[] { selection.CanonicalToken };
        }, ["FullyQualifiedName~OwnerA.Runs"], ["FullyQualifiedName~OwnerBLonger.Runs"]);
        Assert.Equal(0L, parses.CountFor(unrelated));
    }

    [Fact]
    public void BudgetDeclarations_UnchangedThenRewritten_RefreshesMatchesAndSkipsUnrelatedFile()
    {
        var unrelated = Write("tests/Other.Tests/Unrelated.cs", "namespace Demo; class Unrelated {}" + _marker);
        var path = Write("tests/Other.Tests/Alpha.cs", "namespace Demo; class AlphaTests {}" + _marker);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);

        AssertReload(parses, path, "namespace Demo; class AlphaTests {} record AlphaTestsExtra;" + _marker,
            () => FocusedEvidenceRequestResolver.ReadFocusedEvidenceBudgetDeclarations(_root, OtherProject, ["alphatests"])
                .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => (item.Key, item.Value)).ToArray(),
            [("Demo.AlphaTests", "AlphaTests")],
            [("Demo.AlphaTests", "AlphaTests"), ("Demo.AlphaTestsExtra", "AlphaTestsExtra")]);
        Assert.Equal(0L, parses.CountFor(unrelated));
    }

    private static void AssertReload<T>(
        FocusedEvidenceParsedRootCache.ParseObservation parses,
        string path, string rewrittenText, Func<T[]> read, T[] before, T[] after)
    {
        Assert.Equal(before, read());
        Assert.Equal(1L, parses.CountFor(path));
        Assert.Equal(before, read());
        Assert.Equal(1L, parses.CountFor(path));

        var size = new FileInfo(path).Length;
        File.WriteAllText(path, rewrittenText);
        Assert.NotEqual(size, new FileInfo(path).Length);
        Assert.Equal(after, read());
        Assert.Equal(2L, parses.CountFor(path));
    }

    private void WriteLaneManifest() => Write("config/acceptance-manifest.json", $$"""
        { "engine": { "laneTestSourceRoots": ["tests/Lane"] },
          "checks": [{ "name": "lane", "type": "dotnet-test", "project": "{{LaneProject}}" }] }
        """);

    private string Write(string file, string text)
    {
        var path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
