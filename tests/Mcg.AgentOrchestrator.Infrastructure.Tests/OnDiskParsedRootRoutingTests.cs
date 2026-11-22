using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique temporary roots, scoped observers/counters, and no shared cache resets.
public sealed class OnDiskParsedRootRoutingTests : IDisposable
{
    private const string Project = "tests/P/P.csproj";
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();
    private readonly string _marker = "\n// fixture " + Guid.NewGuid().ToString("N");

    [Fact]
    public void ReplayIndex_UnchangedThenRewritten_ReusesAndRefreshesTrees()
    {
        const string file = "tests/P/Tests.cs";
        var path = Path.Combine(_root, file);
        AssertReload(file, "class Before {}", "class AfterWithLongerName {}",
            () => new ImpactReplayTestSourceIndex(new[] { path }).Trees
                .Select(tree => (tree.FilePath, tree.GetRoot().ToFullString())).ToArray(),
            [(path, "class Before {}" + _marker)], [(path, "class AfterWithLongerName {}" + _marker)]);
    }

    [Fact]
    public void DisplayNames_UnchangedThenRewritten_ReusesAndRefreshesNames()
    {
        const string file = "tests/P/Tests.cs";
        AssertReload(file,
            "namespace Ns; class C { [Fact(DisplayName = \"before\")] void M() {} }",
            "namespace Ns; class C { [Fact(DisplayName = \"after with longer name\")] void M() {} }",
            () => new MainTestDisplayNameResolver(_root, [file]).Resolve("Ns.C.M").ToArray(),
            ["before"], ["after with longer name"]);
    }

    [Fact]
    public void DeclaredTypes_UnchangedThenRewritten_ReusesAndRefreshesSet()
    {
        const string file = "src/Types.cs";
        AssertReload(file, "class Before {}", "class Before {} interface Added {}",
            () => CandidateChangedTypeReferenceReader.ReadDeclaredTypeNames(_root, [file])
                .Order(StringComparer.Ordinal).ToArray(), ["Before"], ["Added", "Before"]);
    }

    [Fact]
    public void ReferencedTypes_UnchangedThenRewritten_ReusesAndRefreshesMatches()
    {
        const string file = "tests/P/Tests.cs";
        var types = new HashSet<string>(StringComparer.Ordinal) { "Before", "Added" };
        AssertReload(file, "class Tests { Before value; }", "class Tests { Before value; Added other; }",
            () => CandidateChangedTypeReferenceReader.MatchReferencedTypes(_root, [file], types).ToArray(),
            ["Before"], ["Added", "Before"]);
    }

    [Fact]
    public void SiblingBound_UnchangedThenRewritten_ReusesAndRefreshesDecision()
    {
        Write(Project, "<Project />");
        var siblings = Enumerable.Range(0, 17).Select(index => "Sibling" + index).ToArray();
        var bound = new FocusedEvidenceSiblingExpansionBound(32);
        const string file = "tests/P/Tests.cs";
        string[] Read()
        {
            var result = bound.Decide(_root, Project, siblings, [file]);
            return result.KeptSiblings.Append(result.Reason).ToArray();
        }
        AssertReload(file, "class Sibling0 {}", "class Sibling10 {} class Sibling11 {}", Read,
            ["Sibling0", "bounded family=17 kept=1"],
            ["Sibling10", "Sibling11", "bounded family=17 kept=2"]);
    }

    [Fact]
    public void RatchetAuthority_UnchangedThenRewritten_ReusesAndRefreshesCeilings()
    {
        (string? Path, string? Class, int Lines, int Parts)[] Read() =>
            SourceSizeRatchetPreflight.TryReadAuthority(_root)!
                .Select(row => ((string?)row.RelativePath, (string?)null, row.MaximumLineCount, 0))
                .Concat(SourceSizeRatchetPreflight.TryReadClassAuthority(_root)
                    .Select(row => ((string?)null, (string?)row.ClassName,
                        row.MaximumTotalLineCount, row.MaximumPartialFileCount))).ToArray();
        AssertReload(SourceSizeRatchet.SourcePath,
            "class Limits { object a = new SourceSizeCeiling(\"a.cs\", 10); " +
            "object b = new SourceClassCeiling(\"Tests\", 20, 1); }",
            "class Limits { object a = new SourceSizeCeiling(\"a.cs\", 100); " +
            "object b = new SourceClassCeiling(\"Tests\", 200, 2); }", Read,
            [("a.cs", null, 10, 0), (null, "Tests", 20, 1)],
            [("a.cs", null, 100, 0), (null, "Tests", 200, 2)]);
    }

    [Fact]
    public void ProjectScanner_UnchangedThenRewritten_CountsOnlyRealParses()
    {
        Write(Project, "<Project />");
        const string file = "tests/P/Tests.cs";
        var path = Write(file, "class Before { [Xunit.Fact] public void Runs() {} }");
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        using var counter = AcceptanceTestClassSourceScanner.CountParsesForTests();
        var first = AcceptanceTestClassSourceScanner.ScanProject(_root, Project);
        Assert.Equal(new[] { new AcceptanceTestClassDescriptor("Before", null) }, first);
        Assert.Equal(1, parses.CountFor(path));
        var firstCount = counter.Count;
        Assert.True(firstCount >= 1);
        var second = AcceptanceTestClassSourceScanner.ScanProject(_root, Project);
        Assert.Equal(first, second);
        Assert.Equal(1, parses.CountFor(path));
        Assert.Equal(firstCount, counter.Count);

        Rewrite(path, "class AfterWithLongerName { [Xunit.Fact] public void Runs() {} }");
        var third = AcceptanceTestClassSourceScanner.ScanProject(_root, Project);
        Assert.Equal(new[] { new AcceptanceTestClassDescriptor("AfterWithLongerName", null) }, third);
        Assert.Equal(2, parses.CountFor(path));
        Assert.True(counter.Count > firstCount);
    }

    [Fact]
    public void DeferredProbe_UnchangedThenRewritten_ReusesAndRefreshesTestPresence()
    {
        AssertReload("tests/P/Tests.cs", "class Tests { [Xunit.Fact] public void Runs() {} }",
            "class Tests { public void HelperWithoutAttribute() {} }",
            () => new[] { new DeferredTestClassTestMethodProbe()
                .DeclaresTestMethod(Path.Combine(_root, "tests", "P"), "Tests") }, [true], [false]);
    }

    [Fact]
    public void ReplayIndex_PreloadedAlias_PreservesRequestedFilePath()
    {
        var path = Write("tests/P/Tests.cs", "class Tests {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        var alias = Path.Combine(Path.GetDirectoryName(path)!, ".", "Tests.cs");
        FocusedEvidenceRequestResolver.ParseCSharpRoot(alias);
        var tree = Assert.Single(new ImpactReplayTestSourceIndex(new[] { path }).Trees);
        Assert.Equal(path, tree.FilePath);
        Assert.Equal("class Tests {}" + _marker, tree.GetRoot().ToFullString());
        Assert.Equal(1, parses.CountFor(path));
    }

    [Fact]
    public void ProjectScanner_PreloadedRoot_DoesNotCountCacheHit()
    {
        Write(Project, "<Project />");
        var path = Write("tests/P/Tests.cs", "class Tests { [Xunit.Fact] public void Runs() {} }");
        // Warm the separate extract cache so its in-memory parses do not enter this counter.
        var expected = AcceptanceTestClassSourceScanner.Scan(_root, [path]);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        FocusedEvidenceRequestResolver.ParseCSharpRoot(path);
        using var counter = AcceptanceTestClassSourceScanner.CountParsesForTests();
        Assert.Equal(expected, AcceptanceTestClassSourceScanner.ScanProject(_root, Project));
        Assert.Equal(new[] { new AcceptanceTestClassDescriptor("Tests", null) }, expected);
        Assert.Equal(0, counter.Count);
        Assert.Equal(1, parses.CountFor(path));
    }

    private void AssertReload<T>(string file, string before, string after, Func<T[]> read,
        T[] expectedBefore, T[] expectedAfter)
    {
        var path = Write(file, before);
        using var parses = FocusedEvidenceParsedRootCache.Observe(_root);
        var first = read();
        Assert.Equal(expectedBefore, first);
        Assert.Equal(1, parses.CountFor(path));
        var second = read();
        Assert.Equal(first, second);
        Assert.Equal(1, parses.CountFor(path));
        Rewrite(path, after);
        Assert.Equal(expectedAfter, read());
        Assert.Equal(2, parses.CountFor(path));
    }

    private string Write(string file, string text)
    {
        var path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, WithFixtureMarker(path, text));
        return path;
    }

    private void Rewrite(string path, string text)
    {
        var size = new FileInfo(path).Length;
        File.WriteAllText(path, WithFixtureMarker(path, text));
        Assert.NotEqual(size, new FileInfo(path).Length);
    }

    private string WithFixtureMarker(string path, string text) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? text + _marker : text;

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
