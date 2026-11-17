using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json.Nodes;

// Parallel-safe: cache scopes and temporary directories belong to individual tests.
public sealed class SourceReaderAnalysisCacheTests
{
    private const string Snapshot = "public static class RepositorySourceSnapshotCache { public static object Get(string root) => null; }";
    private static readonly AcceptanceTestClassDescriptor[] Classes =
    [new("Sample.FirstTests", null), new("Sample.SecondTests", null), new("Sample.ThirdTests", null)];

    [Theory]
    [InlineData("readers")]
    [InlineData("null")]
    [InlineData("empty")]
    public void UnchangedRead_ReusesPersistedAnalysisAndPreservesReport(string scenario)
    {
        using var storage = new CacheDirectory();
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", scenario == "readers" ? Reader("FirstTests") + Reader("SecondTests")
            : "namespace Sample { public class OrdinaryTests { [Fact] public void Works() {} } }");
        if (scenario != "null") fixture.Add("Snapshot.cs", Snapshot);
        var sources = fixture.Sources;
        var classes = fixture.Classes;
        var cache = new SourceReaderAnalysisCache(storage.Path);
        ImpactReplaySourceGuardReport? miss;
        using (SourceReaderAnalysisCache.Use(cache))
        {
            miss = SourceReadingGuardTestClasses.Read(sources, classes);
            Assert.Equal(1, cache.AnalysisCount);
            AssertReport(miss, SourceReadingGuardTestClasses.Read(sources, classes));
            Assert.Equal(1, cache.AnalysisCount);
        }

        if (scenario == "null") Assert.Null(miss);
        else Assert.Equal(scenario == "readers" ? new[] { "Sample.FirstTests", "Sample.SecondTests" } : [],
            Assert.IsType<ImpactReplaySourceGuardReport>(miss).Classes);

        var fresh = new SourceReaderAnalysisCache(storage.Path);
        using (SourceReaderAnalysisCache.Use(fresh))
        {
            AssertReport(miss, SourceReadingGuardTestClasses.Read(sources, classes));
            var narrower = classes.Where(item => item.FullName == "Sample.SecondTests").ToArray();
            var filtered = SourceReadingGuardTestClasses.Read(sources, narrower);
            if (scenario == "null") Assert.Null(filtered);
            else Assert.Equal(scenario == "readers" ? new[] { "Sample.SecondTests" } : [],
                Assert.IsType<ImpactReplaySourceGuardReport>(filtered).Classes);
            Assert.Equal(0, fresh.AnalysisCount);
        }
    }

    [Fact]
    public void FilteredMiss_StoresOwnersOutsideTheRequestedClasses()
    {
        using var storage = new CacheDirectory();
        var sources = Sources(Snapshot, Reader("FirstTests"), Reader("SecondTests"));
        using (SourceReaderAnalysisCache.Use(new(storage.Path)))
            Assert.Empty(Assert.IsType<ImpactReplaySourceGuardReport>(
                SourceReadingGuardTestClasses.Read(sources, [])).Classes);

        var fresh = new SourceReaderAnalysisCache(storage.Path);
        using var scope = SourceReaderAnalysisCache.Use(fresh);
        Assert.Equal(new[] { "Sample.FirstTests", "Sample.SecondTests" },
            Assert.IsType<ImpactReplaySourceGuardReport>(SourceReadingGuardTestClasses.Read(sources, Classes)).Classes);
        Assert.Equal(0, fresh.AnalysisCount);
    }

    [Fact]
    public void ChangedSourceMultiset_RecomputesWhileOrderAndPathsReuseAnalysis()
    {
        using var storage = new CacheDirectory();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        using var scope = SourceReaderAnalysisCache.Use(cache);
        string[] baseline = [Snapshot, Reader("FirstTests"), Reader("SecondTests"), "// spare"];
        var sources = Sources(baseline);
        var key = SourceReaderAnalysisCache.Key(sources);
        AssertNames(sources, "Sample.FirstTests", "Sample.SecondTests");
        Assert.Equal(1, cache.AnalysisCount);

        var relocated = new ImpactReplayTestSourceIndex(baseline.Reverse().Select((text, index) =>
            CSharpSyntaxTree.ParseText(text, path: "another-checkout/" + index + ".cs")));
        Assert.Equal(key, SourceReaderAnalysisCache.Key(relocated));
        AssertNames(relocated, "Sample.FirstTests", "Sample.SecondTests");
        Assert.Equal(1, cache.AnalysisCount);

        (string[] Texts, string[] Expected)[] changes =
        [
            ([Snapshot, Reader("FirstTests", reads: false), baseline[2], baseline[3]], ["Sample.SecondTests"]),
            ([.. baseline, Reader("ThirdTests")], ["Sample.FirstTests", "Sample.SecondTests", "Sample.ThirdTests"]),
            ([.. baseline, baseline[3]], ["Sample.FirstTests", "Sample.SecondTests"]),
            ([Snapshot, baseline[1], baseline[3]], ["Sample.FirstTests"])
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        long expectedCount = 1;
        foreach (var change in changes)
        {
            var changed = Sources(change.Texts);
            Assert.True(seen.Add(SourceReaderAnalysisCache.Key(changed)), "Each changed multiset needs a new key.");
            AssertNames(changed, change.Expected);
            Assert.Equal(++expectedCount, cache.AnalysisCount);
        }
    }

    [Theory]
    [InlineData("// a", "// a ")]
    [InlineData("// a\n", "// a\r\n")]
    [InlineData("// é", "// e\u0301")]
    public void ExactTextDifference_ChangesKey(string first, string second)
    {
        Assert.NotEqual(SourceReaderAnalysisCache.Key(Sources(first)), SourceReaderAnalysisCache.Key(Sources(second)));
    }

    [Fact]
    public void TreeBoundariesAndExactCodeUnits_ChangeKey()
    {
        Assert.NotEqual(SourceReaderAnalysisCache.Key(Sources("// a", "// bc")),
            SourceReaderAnalysisCache.Key(Sources("// ab", "// c")));
        Assert.NotEqual(SourceReaderAnalysisCache.Key(Sources("// \ud800")),
            SourceReaderAnalysisCache.Key(Sources("// \ud801")));
        Assert.NotEqual(SourceReaderAnalysisCache.Key(Sources("// ab")),
            SourceReaderAnalysisCache.Key(Sources("// ab", "")));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("version")]
    [InlineData("missing-flag")]
    [InlineData("null")]
    [InlineData("key")]
    [InlineData("null-owners")]
    [InlineData("null-owner")]
    [InlineData("wrong-shape")]
    public void InvalidEntry_RecomputesCorrectReportAndRepairsPersistence(string damage)
    {
        using var storage = new CacheDirectory();
        var sources = Sources(Snapshot, Reader("FirstTests"));
        var cache = new SourceReaderAnalysisCache(storage.Path);
        using (SourceReaderAnalysisCache.Use(cache)) AssertNames(sources, "Sample.FirstTests");
        var entry = Assert.IsType<string>(cache.EntryPath(SourceReaderAnalysisCache.Key(sources)));
        var valid = File.ReadAllText(entry);
        var envelope = Assert.IsType<JsonObject>(JsonNode.Parse(valid));
        switch (damage)
        {
            case "version": envelope["FormatVersion"] = SourceReaderAnalysisCache.FormatVersion + 1; break;
            case "missing-flag": Assert.True(envelope.Remove("DeclaresSnapshotCache")); break;
            case "key": envelope["Key"] = "wrong-key"; break;
            case "null-owners": envelope["ReaderOwners"] = null; break;
            case "null-owner": envelope["ReaderOwners"] = new JsonArray((JsonNode?)null); break;
            case "wrong-shape": envelope["DeclaresSnapshotCache"] = "yes"; break;
        }
        File.WriteAllText(entry, damage switch
        {
            "corrupt" => "not JSON",
            "truncated" => valid[..(valid.Length / 2)],
            "null" => "null",
            _ => envelope.ToJsonString()
        });

        var fresh = new SourceReaderAnalysisCache(storage.Path);
        using (SourceReaderAnalysisCache.Use(fresh)) AssertNames(sources, "Sample.FirstTests");
        Assert.Equal(1, fresh.AnalysisCount);
        var repaired = new SourceReaderAnalysisCache(storage.Path);
        using (SourceReaderAnalysisCache.Use(repaired)) AssertNames(sources, "Sample.FirstTests");
        Assert.Equal(0, repaired.AnalysisCount);
        Assert.Empty(Directory.GetFiles(storage.Path, "*.tmp"));
    }

    [Fact]
    public void UnwritableDirectory_ReadStillReturnsAnalysis()
    {
        using var storage = new CacheDirectory();
        var obstruction = System.IO.Path.Combine(storage.Path, "ordinary-file");
        File.WriteAllText(obstruction, "not a directory");
        var cache = new SourceReaderAnalysisCache(System.IO.Path.Combine(obstruction, "cache"));
        using var scope = SourceReaderAnalysisCache.Use(cache);
        var sources = Sources(Snapshot, Reader("FirstTests"));
        AssertNames(sources, "Sample.FirstTests");
        AssertNames(sources, "Sample.FirstTests");
        Assert.Equal(2, cache.AnalysisCount);
    }

    [Fact]
    public void AnalysisFailure_PropagatesWithoutPublishingEntry()
    {
        using var storage = new CacheDirectory();
        var cache = new SourceReaderAnalysisCache(storage.Path);
        var failure = new InvalidOperationException("analysis failed");
        var sources = Sources(Snapshot);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            cache.GetOrAnalyze(sources, _ => throw failure)));
        Assert.False(File.Exists(cache.EntryPath(SourceReaderAnalysisCache.Key(sources))));
        Assert.Equal(1, cache.AnalysisCount);
    }

    private static string Reader(string name, bool reads = true) => $$"""
        namespace Sample {
            public class {{name}} { [Fact] public void Works() { {{(reads ? "RepositorySourceSnapshotCache.Get(\"root\");" : "")}} } }
        }
        """;

    private static ImpactReplayTestSourceIndex Sources(params string[] texts) =>
        new(texts.Select(text => (SyntaxTree)CSharpSyntaxTree.ParseText(text)));

    private static void AssertNames(ImpactReplayTestSourceIndex sources, params string[] expected) =>
        Assert.Equal(expected, Assert.IsType<ImpactReplaySourceGuardReport>(
            SourceReadingGuardTestClasses.Read(sources, Classes)).Classes);

    private static void AssertReport(ImpactReplaySourceGuardReport? expected, ImpactReplaySourceGuardReport? actual)
    {
        if (expected is null) Assert.Null(actual);
        else Assert.Equal(expected.Classes, Assert.IsType<ImpactReplaySourceGuardReport>(actual).Classes);
    }

    private sealed class CacheDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("source-reader-analysis-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
