using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: fixtures own their files and caches; shared configuration is fixed at module load.
public sealed class SourceReaderBaselineReuseTests
{
    [Theory]
    [InlineData("value.Twice()", "int Twice(this Bag value) => 2", "Twice")]
    [InlineData("Ext.Twice(value)", "int Twice(this Bag value) => 2", "Twice")]
    [InlineData("value.Map<int>()", "T Map<T>(this Bag value) => default(T)", "Map")]
    public void ExtensionCallForms_TargetDeclaredStaticMember(
        string invocation, string declaration, string methodName)
    {
        var sources = Sources(new Dictionary<string, string>
        {
            ["tests/Ext.cs"] = $"public class Bag {{}} public static class Ext {{ public static {declaration}; }}",
            ["tests/Caller.cs"] = $"public class Caller {{ public void Call(Bag value) {{ _ = {invocation}; }} }}"
        });

        var facts = SourceReadingGuardTestClasses.Extract(sources, null);
        var members = facts.SelectMany(file => file.Members).ToArray();
        var declared = Assert.Single(members, member => member.Owner == "Ext" && member.Name == methodName);
        var caller = Assert.Single(members, member => member.Owner == "Caller" && member.Name == "Call");
        Assert.Equal(declared.Id, Assert.Single(caller.Targets));
        Assert.Equal(declared.Id, Assert.Single(Assert.Single(caller.InvocationTargets)));
        Assert.True(SourceReaderFileFacts.Valid(facts));
    }

    [Fact]
    public void RepositoryTestSourceCopy_WritesValidFacts()
    {
        using var copy = new Storage();
        using var storage = new Storage();
        var repository = VerifiedRepositoryRoot.Find();
        var roots = LaneTestSourceRootResolver.Resolve(repository);
        Assert.NotEmpty(roots);
        Assert.All(roots, root => Assert.StartsWith("tests/", root));
        var originals = LaneTestSourceRootResolver.SourceFiles(repository, roots);
        Assert.True(originals.Length > 1000, $"Expected repository test sources, found {originals.Length}.");
        var copied = SourceReaderRepositoryIncrementalReuseTests.CopySourcesWithDistinctContents(repository, originals, copy.Path);
        var sources = new ImpactReplayTestSourceIndex(copied, null, copy.Path);
        var cache = new SourceReaderAnalysisCache(storage.Path);

        cache.GetOrAnalyze(sources, SourceReadingGuardTestClasses.Extract, SourceReadingGuardTestClasses.Evaluate);

        Assert.Equal(copied.Length, cache.SemanticFileCount);
        var entry = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(cache.EntryPath(SourceReaderAnalysisCache.Key(sources))!)));
        Assert.Equal(SourceReaderAnalysisCache.FormatVersion, entry["FormatVersion"]!.GetValue<int>());
        Assert.False(entry.ContainsKey("Files"));
        Assert.False(entry.ContainsKey("FilesDigest"));
        var files = ReadBaseline(storage.Path, sources);
        Assert.Equal(copied.Length, files.Length);
        Assert.Equal(copied.Select(path => Path.GetRelativePath(copy.Path, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal), files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.True(SourceReaderFileFacts.Valid(files));
    }

    [Theory]
    [InlineData(10, 4, false)]
    [InlineData(9, 4, false)]
    [InlineData(10, 0, false)]
    [InlineData(0, 0, false)]
    [InlineData(10, 5, true)]
    [InlineData(9, 5, true)]
    public void CandidateOverlap_UsesBaselineOnlyAtHalfOrMore(
        int candidateCount, int sharedCount, bool incremental)
    {
        using var storage = new Storage();
        var baseline = Baseline();
        var baselineCache = new SourceReaderAnalysisCache(storage.Path);
        Analyze(baselineCache, baseline);
        Assert.True(SourceReaderFileFacts.Valid(ReadBaseline(storage.Path, Sources(baseline))));
        var candidate = baseline.Take(sharedCount).ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var index = sharedCount; index < candidateCount; index++)
            candidate.Add($"tests/New{index}.cs", $"public class New{index} {{ public void Added{index}() {{}} }}");
        var cache = new SourceReaderAnalysisCache(storage.Path);
        Assert.False(cache.TryRead(SourceReaderAnalysisCache.Key(Sources(candidate)), out _));
        var extractions = new List<HashSet<string>?>();

        var actual = Analyze(cache, candidate, extractions);

        AssertEquivalent(Analyze(new(null), candidate), actual);
        var extracted = Assert.Single(extractions);
        if (incremental)
        {
            Assert.NotNull(extracted);
            Assert.Equal(candidate.Keys.Except(baseline.Keys).Order(StringComparer.Ordinal),
                extracted.Order(StringComparer.Ordinal));
            Assert.Equal(candidateCount - sharedCount, cache.SemanticFileCount);
            Assert.True(cache.SemanticFileCount < candidateCount);
        }
        else
        {
            // A non-null extraction set proves Plan ran, even when it selected every candidate.
            Assert.Null(extracted);
            Assert.Equal(candidateCount, cache.SemanticFileCount);
        }
    }

    [Fact]
    public void PreviousFormatVersion_IsNeitherBaselineNorExactHit()
    {
        using var storage = new Storage();
        var baseline = Baseline();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        Analyze(cache, baseline);
        var key = SourceReaderAnalysisCache.Key(Sources(baseline));
        var path = cache.EntryPath(key)!;
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True(SourceReaderFileFacts.Valid(ReadBaseline(storage.Path, Sources(baseline))));
        Assert.True(cache.TryRead(key, out _));
        // Pin the operator's compatibility boundary; a relative version alone cannot detect a reverted bump.
        Assert.Equal(11, SourceReaderAnalysisCache.FormatVersion);
        const int previousFormatVersion = 10;
        // Change only the version so the fixture isolates the compatibility check.
        entry["FormatVersion"] = previousFormatVersion;
        File.WriteAllText(path, entry.ToJsonString());
        Assert.False(cache.TryRead(key, out _));
        var store = new SourceReaderFileFactsStore(storage.Path);
        foreach (var text in baseline.Values)
        {
            var recordPath = store.RecordPath(SourceReaderFileFacts.Hash(text))!;
            var record = JsonNode.Parse(File.ReadAllText(recordPath))!;
            record["FormatVersion"] = previousFormatVersion;
            File.WriteAllText(recordPath, record.ToJsonString());
        }
        var candidate = new Dictionary<string, string>(baseline)
        {
            ["tests/Stable1.cs"] = "public class Stable1 { public void Changed() {} }"
        };
        var extractions = new List<HashSet<string>?>();
        var freshCache = new SourceReaderAnalysisCache(storage.Path);

        var actual = Analyze(freshCache, candidate, extractions);

        Assert.Null(Assert.Single(extractions));
        Assert.Equal(candidate.Count, freshCache.SemanticFileCount);
        AssertEquivalent(Analyze(new(null), candidate), actual);
        Assert.Equal(previousFormatVersion, JsonNode.Parse(File.ReadAllText(path))!["FormatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void SharedCache_UsesAssemblyOwnedRootAndPreservesScopePrecedence()
    {
        Assert.Null(AssemblySourceReaderAnalysisCacheIsolation.InstallationError);
        Assert.True(AssemblySourceReaderAnalysisCacheIsolation.Installed);
        var shared = SourceReaderAnalysisCache.Current;
        var directory = Path.GetDirectoryName(shared.EntryPath("probe"))!;
        Assert.True(IsWithin(directory, AssemblySourceReaderAnalysisCacheIsolation.Root));
        var processTemp = Path.GetTempPath();
        if (OperatingSystem.IsWindows() && File.Exists(AssemblyTempRedirect.RootLeasePath(processTemp)))
            Assert.Equal(Path.Combine(processTemp, "source-reader-analysis"), directory);
        var localRoots = new[]
        {
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        }.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray();
        Assert.NotEmpty(localRoots);
        Assert.All(localRoots, local => Assert.False(IsWithin(directory, Path.Combine(local!, "Mcg.AgentOrchestrator"))));
        using var storage = new Storage();
        var explicitCache = new SourceReaderAnalysisCache(storage.Path);
        Assert.Equal(Path.Combine(storage.Path, "probe.json"), explicitCache.EntryPath("probe"));
        using (SourceReaderAnalysisCache.Use(explicitCache))
            Assert.Same(explicitCache, SourceReaderAnalysisCache.Current);
        Assert.Same(shared, SourceReaderAnalysisCache.Current);
        Assert.False(SourceReaderAnalysisCache.OverrideDefaultDirectory(storage.Path));
        Assert.Equal(directory, Path.GetDirectoryName(SourceReaderAnalysisCache.Current.EntryPath("probe")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void DefaultDirectoryOverride_BlankPathIsRejected(string directory)
    {
        Assert.Throws<ArgumentException>(() => SourceReaderAnalysisCache.OverrideDefaultDirectory(directory));
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> Baseline()
    {
        var files = new Dictionary<string, string>
        {
            ["tests/Snapshot.cs"] = "public static class RepositorySourceSnapshotCache { public static object Get(string root) => null; }"
        };
        for (var index = 1; index < 10; index++)
            files.Add($"tests/Stable{index}.cs", $"public class Stable{index} {{ public void Unique{index}() {{}} }}");
        return files;
    }

    private static ImpactReplayTestSourceIndex Sources(IReadOnlyDictionary<string, string> files) =>
        new(files.Select(pair => (SyntaxTree)CSharpSyntaxTree.ParseText(pair.Value, path: pair.Key)));

    private static SourceReaderFileFacts[] ReadBaseline(string storagePath, ImpactReplayTestSourceIndex sources)
    {
        var identities = sources.Trees.Select(tree => (SourceReaderFileFacts.RelativePath(sources, tree),
            SourceReaderFileFacts.Hash(tree.GetText().ToString()))).ToArray();
        return Assert.IsType<SourceReaderFileFacts[]>(new SourceReaderFileFactsStore(storagePath).FindBaseline(identities, out _));
    }

    private static SourceReaderAnalysisCache.Analysis Analyze(SourceReaderAnalysisCache cache,
        IReadOnlyDictionary<string, string> files, List<HashSet<string>?>? extractions = null) =>
        cache.GetOrAnalyze(Sources(files), (sources, paths) =>
        {
            extractions?.Add(paths is null ? null : new(paths, StringComparer.Ordinal));
            return SourceReadingGuardTestClasses.Extract(sources, paths);
        }, SourceReadingGuardTestClasses.Evaluate);

    private static void AssertEquivalent(SourceReaderAnalysisCache.Analysis expected, SourceReaderAnalysisCache.Analysis actual)
    {
        Assert.Equal(expected.DeclaresSnapshotCache, actual.DeclaresSnapshotCache);
        Assert.Equal(expected.ReaderOwners, actual.ReaderOwners);
    }

    private sealed class Storage : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("source-reader-baseline-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
