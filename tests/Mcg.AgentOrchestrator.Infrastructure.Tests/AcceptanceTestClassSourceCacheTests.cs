using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its filesystem tree and AsyncLocal cache/policy scopes.
public sealed class AcceptanceTestClassSourceCacheTests
{
    private const string TestProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
    private const string SupportProject = "tests/Mcg.AgentOrchestrator.TestSupport/";

    [Fact]
    public void SharedStore_SecondInstance_ParsesNothingAndMatches()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var directory = tree.PathOf("store");
        var first = new AcceptanceTestClassSourceCache(directory);
        var expected = Scan(tree, first);
        Assert.Equal(paths.Length, first.ParsedFileCount);
        Assert.Equal(paths.Length, Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Length);

        var second = new AcceptanceTestClassSourceCache(directory);
        var actual = Scan(tree, second);

        Assert.NotEmpty(actual);
        Assert.Equal(expected, actual);
        Assert.Equal(0, second.ParsedFileCount);
        Assert.All(paths, path => Assert.Equal(0, second.ParseCountForTests(tree.Root, path)));
    }

    [Fact]
    public void OwnerState_DefaultInstances_ReusePersistedExtracts()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        tree.Write(".orchestrator/owner-marker", "fixture owner state");
        var first = new AcceptanceTestClassSourceCache();
        var expected = Scan(tree, first);
        Assert.Equal(paths.Length, first.ParsedFileCount);
        Assert.Equal(paths.Length, Directory.GetFiles(tree.PathOf(Path.Combine(".orchestrator",
            AcceptanceTestClassSourceExtractStore.DirectoryName)), "*.json", SearchOption.AllDirectories).Length);
        var next = new AcceptanceTestClassSourceCache();

        Assert.Equal(expected, Scan(tree, next));
        Assert.Equal(0, next.ParsedFileCount);
    }

    [Fact]
    public void SharedStore_EditedFile_NextInstanceParsesExactlyOne()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var directory = tree.PathOf("store");
        Scan(tree, new AcceptanceTestClassSourceCache(directory));
        var changed = tree.Write(SupportProject + "Names.cs", Names("Beta"));
        var next = new AcceptanceTestClassSourceCache(directory);

        var actual = Scan(tree, next);

        Assert.Equal(1, next.ParsedFileCount);
        Assert.Equal(1, next.ParseCountForTests(tree.Root, changed));
        Assert.All(paths.Except([changed]), path => Assert.Equal(0, next.ParseCountForTests(tree.Root, path)));
        Assert.Equal("Beta", Assert.Single(actual, item => item.FullName == "DerivedTests").Collection);
        Assert.Equal(Scan(tree, new AcceptanceTestClassSourceCache(null)), actual);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("non-json")]
    [InlineData("version")]
    [InlineData("ancestry")]
    public void SharedStore_InvalidEntry_NextInstanceParsesAndRepairs(string defect)
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var directory = tree.PathOf("store");
        var expected = Scan(tree, new AcceptanceTestClassSourceCache(null));
        Assert.Equal(expected, Scan(tree, new AcceptanceTestClassSourceCache(directory)));
        var store = new AcceptanceTestClassSourceExtractStore(directory);
        var hash = AcceptanceTestClassSourceExtractStore.HashText(File.ReadAllText(paths[0]));
        var path = store.EntryPath(hash);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        var entry = JsonNode.Parse(json)!;
        entry["ExtractorVersion"] = AcceptanceTestClassSourceExtractStore.ExtractorVersion + 1;
        if (defect == "ancestry")
        {
            entry["ExtractorVersion"] = AcceptanceTestClassSourceExtractStore.ExtractorVersion;
            entry["Extract"]!["Ancestry"] = null;
        }
        File.WriteAllText(path, defect switch
        {
            "truncated" => json[..(json.Length / 2)],
            "non-json" => "not JSON",
            _ => entry.ToJsonString()
        });
        Assert.False(store.TryRead(hash, out _));
        var next = new AcceptanceTestClassSourceCache(directory);

        var actual = Scan(tree, next);

        Assert.Equal(expected, actual);
        Assert.Equal(1, next.ParsedFileCount);
        Assert.Equal(1, next.ParseCountForTests(tree.Root, paths[0]));
        Assert.True(store.TryRead(hash, out _));
        var repaired = new AcceptanceTestClassSourceCache(directory);
        Assert.Equal(expected, Scan(tree, repaired));
        Assert.Equal(0, repaired.ParsedFileCount);
    }

    [Theory]
    [InlineData("Literal")]
    [InlineData("NameOf")]
    [InlineData("Member")]
    [InlineData("Identifier")]
    public void SharedStore_NullCollectionValue_NextInstanceParsesAndRepairs(string kindName)
    {
        var kind = Enum.Parse<AcceptanceTestClassSourceScanner.CollectionExpressionKind>(kindName);
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var directory = tree.PathOf("store");
        var expected = Scan(tree, new AcceptanceTestClassSourceCache(null));
        Assert.Equal(expected, Scan(tree, new AcceptanceTestClassSourceCache(directory)));
        var store = new AcceptanceTestClassSourceExtractStore(directory);
        var hash = AcceptanceTestClassSourceExtractStore.HashText(File.ReadAllText(paths[1]));
        Assert.True(store.TryRead(hash, out _));
        var path = store.EntryPath(hash);
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        var collection = entry["Extract"]!["Classes"]![0]!["Collection"]!;
        collection["Kind"] = (int)kind;
        collection["Value"] = null;
        File.WriteAllText(path, entry.ToJsonString());
        var next = new AcceptanceTestClassSourceCache(directory);

        Assert.Equal(expected, Scan(tree, next));
        Assert.Equal(1, next.ParsedFileCount);
        Assert.Equal(1, next.ParseCountForTests(tree.Root, paths[1]));
        Assert.True(store.TryRead(hash, out var repaired));
        var repairedCollection = Assert.Single(repaired.Classes).Collection;
        Assert.Equal(AcceptanceTestClassSourceScanner.CollectionExpressionKind.Member, repairedCollection.Kind);
        Assert.Equal("Names.Lane", repairedCollection.Value);
        var warm = new AcceptanceTestClassSourceCache(directory);
        Assert.Equal(expected, Scan(tree, warm));
        Assert.Equal(0, warm.ParsedFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedStore_BlockedDirectory_MatchesStorelessScan(bool blockVersion)
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var expected = Scan(tree, new AcceptanceTestClassSourceCache(null));
        var blocked = blockVersion ? Path.Combine("store",
            AcceptanceTestClassSourceExtractStore.ExtractorVersion.ToString()) : "store";
        var blockedPath = tree.Write(blocked, "directory is blocked");
        var cache = new AcceptanceTestClassSourceCache(tree.PathOf("store"));

        Assert.Equal(expected, Scan(tree, cache));
        Assert.Equal(paths.Length, cache.ParsedFileCount);
        Assert.Equal("directory is blocked", File.ReadAllText(blockedPath));
    }

    private static IReadOnlyList<AcceptanceTestClassDescriptor> Scan(
        TestTree tree, AcceptanceTestClassSourceCache cache)
    {
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        return AcceptanceTestClassSourceScanner.Scan(tree.Root);
    }

    [Fact]
    public void UnchangedTree_SecondScan_ParsesNothingAndMatches()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);

        var first = AcceptanceTestClassSourceScanner.Scan(tree.Root);
        Assert.Equal(paths.Length, cache.ParsedFileCount);
        Assert.Equal("Alpha", Assert.Single(first, item => item.FullName == "AlphaTests").Collection);
        Assert.Equal("Alpha", Assert.Single(first, item => item.FullName == "DerivedTests").Collection);
        Assert.Equal(["AlphaTests", "DerivedTests", "PlainTests"], first.Select(item => item.FullName));

        var second = AcceptanceTestClassSourceScanner.Scan(tree.Root);

        Assert.Equal(first, second);
        Assert.Equal(paths.Length, cache.ParsedFileCount);
        Assert.All(paths, path => Assert.Equal(1, cache.ParseCountForTests(tree.Root, path)));
    }

    [Fact]
    public void ChangedAddedRemovedFiles_NextScan_ParsesOnlyChangedAndAdded()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        AcceptanceTestClassSourceScanner.Scan(tree.Root);
        var count = cache.ParsedFileCount;

        var constantPath = tree.PathOf(SupportProject + "Names.cs");
        var timestamp = File.GetLastWriteTimeUtc(constantPath);
        tree.Write(SupportProject + "Names.cs", Names("Beta"));
        File.SetLastWriteTimeUtc(constantPath, timestamp.AddSeconds(2));
        var added = tree.Write(TestProject + "GammaTests.cs", Consumer("GammaTests", "Names.Lane"));
        var removed = tree.PathOf(TestProject + "PlainTests.cs");
        File.Delete(removed);

        var actual = AcceptanceTestClassSourceScanner.Scan(tree.Root);

        Assert.Equal(2, cache.ParsedFileCount - count);
        Assert.Equal(2, cache.ParseCountForTests(tree.Root, constantPath));
        Assert.Equal(1, cache.ParseCountForTests(tree.Root, added));
        Assert.All(paths.Except([constantPath, removed]),
            path => Assert.Equal(1, cache.ParseCountForTests(tree.Root, path)));
        Assert.DoesNotContain(removed, cache.CachedPathsForTests(tree.Root));
        Assert.Equal(paths.Length, cache.CachedPathsForTests(tree.Root).Count);
        Assert.DoesNotContain(actual, item => item.FullName == "PlainTests");
        Assert.All(actual, item => Assert.Equal("Beta", item.Collection));

        // An empty private cache forces every current file through the parser again.
        var uncached = new AcceptanceTestClassSourceCache();
        using (AcceptanceTestClassSourceCache.Use(uncached))
            Assert.Equal(AcceptanceTestClassSourceScanner.Scan(tree.Root), actual);
        Assert.Equal(paths.Length, uncached.ParsedFileCount);
    }

    [Fact]
    public void PlanIdentity_UnchangedTree_IsCacheInvariantAndParsesEachFileOnce()
    {
        using var tree = new TestTree();
        var paths = tree.WriteSources();
        tree.Write(TestProject + "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "<Project />");
        tree.Write("config/acceptance-manifest.json", """
            {
              "version": 1,
              "engine": {
                "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests", "tests/Mcg.AgentOrchestrator.TestSupport"],
                "infrastructureTestLanes": [
                  { "name": "Alpha", "filter": "FullyQualifiedName~SeedTests", "ownedCollections": ["Alpha"] },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~SeedTests" }
                ]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
        using var policy = AcceptanceShardPolicySwitches.Use(new(
            FullShards: false, ChangeScoped: false, ReadVariable: _ => null));
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        string[] changed = [TestProject + "AlphaTests.cs"];

        var cold = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(tree.Root, changed);
        Assert.Equal(paths.Length, cache.ParsedFileCount);
        var warm = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(tree.Root, changed);

        Assert.StartsWith("effective-manifest-sha256-", cold, StringComparison.Ordinal);
        Assert.Equal(cold, warm);
        Assert.Equal(paths.Length, cache.ParsedFileCount);
        Assert.All(paths, path => Assert.Equal(1, cache.ParseCountForTests(tree.Root, path)));
        var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(tree.Root, changed);
        var lane = Assert.Single(checks, check => check.Name == "infrastructure tests: Alpha");
        Assert.Contains(lane.Arguments, argument => argument.Contains("FullyQualifiedName=AlphaTests", StringComparison.Ordinal));

        var uncached = new AcceptanceTestClassSourceCache();
        using (AcceptanceTestClassSourceCache.Use(uncached))
            Assert.Equal(cold, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(tree.Root, changed));
        Assert.Equal(paths.Length, uncached.ParsedFileCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangedMetadata_NextScan_InvalidatesLengthOrTimestampIndependently(bool changeLength)
    {
        using var tree = new TestTree();
        var path = tree.Write(TestProject + "Names.cs", Names("Alpha"));
        tree.Write(TestProject + "AlphaTests.cs", Consumer("AlphaTests", "Names.Lane"));
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        AcceptanceTestClassSourceScanner.Scan(tree.Root);
        var before = new FileInfo(path);
        var originalLength = before.Length;
        var timestamp = before.LastWriteTimeUtc;
        var value = changeLength ? "Beta" : "Omega";
        tree.Write(TestProject + "Names.cs", Names(value));
        File.SetLastWriteTimeUtc(path, changeLength ? timestamp : timestamp.AddSeconds(2));
        var after = new FileInfo(path);
        if (changeLength)
        {
            Assert.NotEqual(originalLength, after.Length);
            Assert.Equal(timestamp, after.LastWriteTimeUtc);
        }
        else
        {
            Assert.Equal(originalLength, after.Length);
            Assert.NotEqual(timestamp, after.LastWriteTimeUtc);
        }

        var result = AcceptanceTestClassSourceScanner.Scan(tree.Root);

        Assert.Equal(value, Assert.Single(result).Collection);
        Assert.Equal(3, cache.ParsedFileCount);
        Assert.Equal(2, cache.ParseCountForTests(tree.Root, path));
    }

    [Fact]
    public void WorktreeLimit_Overflow_EvictsLeastRecentlyUsedTree()
    {
        using var tree = new TestTree();
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        var roots = Enumerable.Range(0, AcceptanceTestClassSourceCache.MaxWorktrees + 1)
            .Select(index => tree.PathOf("worktree-" + index)).ToArray();
        foreach (var root in roots)
        {
            tree.Write(System.IO.Path.GetRelativePath(tree.Root,
                System.IO.Path.Combine(root, "config", "acceptance-manifest.json")), SourceManifest);
            var path = System.IO.Path.Combine(root, TestProject, "AlphaTests.cs");
            tree.Write(System.IO.Path.GetRelativePath(tree.Root, path), Consumer("AlphaTests", "\"" + root.Replace('\\', '/') + "\""));
            var result = AcceptanceTestClassSourceScanner.Scan(root);
            Assert.Equal(root.Replace('\\', '/'), Assert.Single(result).Collection);
        }
        Assert.Empty(cache.CachedPathsForTests(roots[0]));
        Assert.All(roots.Skip(1), root => Assert.Single(cache.CachedPathsForTests(root)));
        var count = cache.ParsedFileCount;

        AcceptanceTestClassSourceScanner.Scan(roots[1]);
        Assert.Equal(count, cache.ParsedFileCount);
        AcceptanceTestClassSourceScanner.Scan(roots[0]);

        Assert.Equal(count + 1, cache.ParsedFileCount);
        Assert.Single(cache.CachedPathsForTests(roots[0]));
        Assert.Single(cache.CachedPathsForTests(roots[1]));
        Assert.Empty(cache.CachedPathsForTests(roots[2]));
    }

    [Fact]
    public void RemovedProject_NextScan_DropsAllCachedPaths()
    {
        using var tree = new TestTree();
        tree.Write(TestProject + "AlphaTests.cs", Consumer("AlphaTests", "\"Alpha\""));
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Directory.Delete(tree.PathOf(TestProject), recursive: true);

        Assert.Empty(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.Empty(cache.CachedPathsForTests(tree.Root));
        Assert.Equal(1, cache.ParsedFileCount);
    }

    [Theory]
    [InlineData("\"Alpha\"", "Alpha")]
    [InlineData("nameof(Names.Alpha)", "Alpha")]
    [InlineData("Names.Lane", "Alpha")]
    [InlineData("Lane", "Alpha")]
    [InlineData("Names.Missing", null)]
    [InlineData("42", null)]
    [InlineData("", null)]
    public void CollectionExpression_WarmScan_PreservesValuesAndResolutionErrors(string expression, string? expected)
    {
        using var tree = new TestTree();
        tree.Write(TestProject + "Names.cs", Names("Alpha"));
        tree.Write(TestProject + "AlphaTests.cs", Consumer("AlphaTests", expression));
        var cache = new AcceptanceTestClassSourceCache();
        using var scope = AcceptanceTestClassSourceCache.Use(cache);
        for (var scan = 0; scan < 2; scan++)
        {
            if (expected is null)
            {
                var error = Assert.Throws<InvalidDataException>(() => AcceptanceTestClassSourceScanner.Scan(tree.Root));
                Assert.Equal("Cannot resolve test collection on 'AlphaTests'.", error.Message);
            }
            else
                Assert.Equal(expected, Assert.Single(AcceptanceTestClassSourceScanner.Scan(tree.Root)).Collection);
        }
        Assert.Equal(2, cache.ParsedFileCount);
    }

    private static string Names(string value) =>
        $"internal static class Names {{ internal const string Lane = \"{value}\"; }}";

    private static string Consumer(string name, string expression) =>
        $"[Xunit.Collection({expression})] public class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}";

    private const string SourceManifest = """
        {"engine":{"laneTestSourceRoots":[
          "tests/Mcg.AgentOrchestrator.Infrastructure.Tests",
          "tests/Mcg.AgentOrchestrator.TestSupport"
        ]}}
        """;

    private sealed class TestTree : IDisposable
    {
        internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "acceptance-source-cache-" + Guid.NewGuid().ToString("N"));

        internal TestTree() => Write("config/acceptance-manifest.json", SourceManifest);

        internal string PathOf(string relativePath) => System.IO.Path.Combine(Root, relativePath);

        internal string Write(string relativePath, string source)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            return path;
        }

        internal string[] WriteSources() =>
        [
            Write(SupportProject + "Names.cs", Names("Alpha")),
            Write(TestProject + "AlphaTests.cs", Consumer("AlphaTests", "Names.Lane")),
            Write(TestProject + "PlainTests.cs", "public class PlainTests { [Xunit.Fact] public void Runs() {} }"),
            Write(SupportProject + "BaseFacts.cs",
                "[Xunit.Collection(Names.Lane)] public abstract class BaseFacts { [Xunit.Fact] public void Runs() {} }"),
            Write(TestProject + "DerivedTests.cs", "public class DerivedTests : BaseFacts {}")
        ];

        public void Dispose()
        {
            // Only delete the exact unique fixture root allocated by this instance.
            Directory.Delete(Root, recursive: true);
        }
    }
}
