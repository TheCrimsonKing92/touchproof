using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: unique fixture roots; scans use an AsyncLocal private cache.
public sealed class AcceptanceTestClassSourceExtractStoreTests
{
    [Fact]
    public void StoredExtract_RepresentativeSources_RoundTripsEveryField()
    {
        using var tree = new StoreTree();
        const string project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
        tree.Write("config/acceptance-manifest.json",
            """{"engine":{"laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"]}}""");
        string[] sources = [
            """
            namespace Outer { namespace Inner {
                internal static class Names { public const string Lane = "Alpha"; }
                [Xunit.Collection(Names.Lane)] public class Collected {
                    [Xunit.Fact] public void Runs() {}
                    public class Nested { [Xunit.Theory] public void Runs() {} }
                }
            } }
            """,
            """
            global using Xunit;
            namespace Outer.Inner;
            public abstract class BaseFacts { [Fact] public void Inherited() {} }
            """,
            """
            using Parent = Outer.Inner.BaseFacts;
            namespace Other;
            public class Derived : Parent {}
            """
        ];
        var paths = sources.Select((source, index) => tree.Write(project + index + ".cs", source)).ToArray();
        var fresh = new AcceptanceTestClassSourceCache(null);
        using (AcceptanceTestClassSourceCache.Use(fresh))
            Assert.NotEmpty(AcceptanceTestClassSourceScanner.Scan(tree.Root));
        Assert.Equal(sources.Length, fresh.ParsedFileCount);
        var store = new AcceptanceTestClassSourceExtractStore(tree.PathOf("store"));

        for (var index = 0; index < sources.Length; index++)
        {
            var expected = fresh.ExtractForTests(tree.Root, paths[index]);
            Assert.NotNull(expected.Ancestry);
            var hash = AcceptanceTestClassSourceExtractStore.HashText(sources[index]);
            store.Write(hash, expected);
            Assert.True(File.Exists(store.EntryPath(hash)), "The store must publish an entry.");
            // A new object reads the persisted representation, independently of the writer.
            var reader = new AcceptanceTestClassSourceExtractStore(tree.PathOf("store"));
            Assert.True(reader.TryRead(hash, out var actual));
            AssertExtractEqual(expected, actual);
        }
        var constants = fresh.ExtractForTests(tree.Root, paths[0]);
        Assert.Single(constants.Constants);
        Assert.Contains(constants.Classes, item => item.FullName == "Outer.Inner.Collected+Nested");
        Assert.Contains(fresh.ExtractForTests(tree.Root, paths[2]).Ancestry!.Classes,
            item => item.BaseName == "Parent");
        Assert.Empty(Directory.GetFiles(tree.PathOf("store"), "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("non-json")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("extract")]
    [InlineData("constants")]
    [InlineData("classes")]
    [InlineData("collection")]
    [InlineData("ancestry")]
    [InlineData("missing-ancestry")]
    public void InvalidEntry_Read_IsMissAndCanBeRepaired(string defect)
    {
        using var tree = new StoreTree();
        var store = new AcceptanceTestClassSourceExtractStore(tree.PathOf("store"));
        var extract = ExampleExtract();
        var hash = AcceptanceTestClassSourceExtractStore.HashText("source");
        store.Write(hash, extract);
        var path = store.EntryPath(hash);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        var entry = JsonNode.Parse(json)!;
        switch (defect)
        {
            case "version": entry["ExtractorVersion"] = AcceptanceTestClassSourceExtractStore.ExtractorVersion + 1; break;
            case "hash": entry["ContentHash"] = "foreign"; break;
            case "extract": entry["Extract"] = null; break;
            case "constants": entry["Extract"]!["Constants"] = null; break;
            case "classes": entry["Extract"]!["Classes"] = null; break;
            case "collection": entry["Extract"]!["Classes"]![0]!["Collection"] = null; break;
            case "ancestry": entry["Extract"]!["Ancestry"]!["GlobalUsings"] = null; break;
            case "missing-ancestry": entry["Extract"]!["Ancestry"] = null; break;
        }
        File.WriteAllText(path, defect switch
        {
            "truncated" => json[..(json.Length / 2)],
            "non-json" => "not JSON",
            "null" => "null",
            _ => entry.ToJsonString()
        });

        Assert.False(store.TryRead(hash, out _));
        store.Write(hash, extract);
        Assert.True(store.TryRead(hash, out var repaired));
        AssertExtractEqual(extract, repaired);
    }

    [Theory]
    [InlineData("Literal", false)]
    [InlineData("NameOf", false)]
    [InlineData("Member", false)]
    [InlineData("Identifier", false)]
    [InlineData("None", true)]
    [InlineData("Unresolvable", true)]
    public void CollectionValue_Null_ReadMatchesKindContract(string kindName, bool usable)
    {
        var kind = Enum.Parse<AcceptanceTestClassSourceScanner.CollectionExpressionKind>(kindName);
        using var tree = new StoreTree();
        var store = new AcceptanceTestClassSourceExtractStore(tree.PathOf("store"));
        var hash = AcceptanceTestClassSourceExtractStore.HashText("source");
        store.Write(hash, ExampleExtract());
        Assert.True(store.TryRead(hash, out _));
        var path = store.EntryPath(hash);
        var entry = JsonNode.Parse(File.ReadAllText(path))!;
        var collection = entry["Extract"]!["Classes"]![0]!["Collection"]!;
        collection["Kind"] = (int)kind;
        collection["Value"] = null;
        File.WriteAllText(path, entry.ToJsonString());

        Assert.Equal(usable, store.TryRead(hash, out var actual));
        if (usable)
        {
            Assert.Equal(kind, Assert.Single(actual.Classes).Collection.Kind);
            Assert.Null(Assert.Single(actual.Classes).Collection.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedDirectory_Write_IsBestEffortAndReadIsMiss(bool blockVersion)
    {
        using var tree = new StoreTree();
        var directory = tree.PathOf("store");
        var block = blockVersion ? Path.Combine("store",
            AcceptanceTestClassSourceExtractStore.ExtractorVersion.ToString()) : "store";
        var blockedPath = tree.Write(block, "regular file prevents directory creation");
        var store = new AcceptanceTestClassSourceExtractStore(directory);
        var hash = AcceptanceTestClassSourceExtractStore.HashText("source");

        store.Write(hash, ExampleExtract());

        Assert.False(store.TryRead(hash, out _));
        Assert.Equal("regular file prevents directory creation", File.ReadAllText(blockedPath));
    }

    [Fact]
    public void OwnerResolution_GoalWorktree_UsesOwnerStateAndRegistersCache()
    {
        using var tree = new StoreTree();
        var worktree = tree.PathOf(".orchestrator-worktrees/goal");
        Assert.Null(AcceptanceTestClassSourceExtractStore.Resolve(worktree));
        Directory.CreateDirectory(tree.PathOf(".orchestrator"));
        var store = Assert.IsType<AcceptanceTestClassSourceExtractStore>(
            AcceptanceTestClassSourceExtractStore.Resolve(worktree));
        var hash = AcceptanceTestClassSourceExtractStore.HashText("source");
        Assert.Equal(Path.Combine(tree.Root, ".orchestrator",
            AcceptanceTestClassSourceExtractStore.DirectoryName,
            AcceptanceTestClassSourceExtractStore.ExtractorVersion.ToString(), hash + ".json"), store.EntryPath(hash));
        Assert.Equal(store.EntryPath(hash), AcceptanceTestClassSourceExtractStore.Resolve(tree.Root)!.EntryPath(hash));
        Assert.False(Directory.Exists(worktree));
        var entry = Assert.IsType<OrchestratorArtifactRegistry.Entry>(
            OrchestratorArtifactRegistry.Classify(AcceptanceTestClassSourceExtractStore.DirectoryName));
        Assert.Equal(nameof(AcceptanceTestClassSourceExtractStore), entry.Owner);
        Assert.Equal(512L * 1024 * 1024, entry.BudgetBytes);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.Unmanaged, entry.Retention);
    }

    [Fact]
    public void ContentHash_DecodedText_UsesExactUtf8WithoutNormalization()
    {
        const string source = "// café\r\nclass Example {}";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant(),
            AcceptanceTestClassSourceExtractStore.HashText(source));
        Assert.NotEqual(AcceptanceTestClassSourceExtractStore.HashText(source.Replace("\r\n", "\n")),
            AcceptanceTestClassSourceExtractStore.HashText(source));
    }

    private static AcceptanceTestClassSourceScanner.FileExtract ExampleExtract() => new(
        [new("Names.Lane", "Alpha")],
        [new("Example", "Example", false, null, true,
            new(AcceptanceTestClassSourceScanner.CollectionExpressionKind.Literal, "Alpha"))],
        new([new("Example", "Example", null, ["Fact"], [new("", [new("Xunit", null)])])], []));

    private static void AssertExtractEqual(AcceptanceTestClassSourceScanner.FileExtract expected,
        AcceptanceTestClassSourceScanner.FileExtract actual)
    {
        Assert.Equal(expected.Constants.Select(item => (item.Key, item.Value)),
            actual.Constants.Select(item => (item.Key, item.Value)));
        Assert.Equal(expected.Classes.Select(item => (item.FullName, item.ShortName, item.Abstract,
                item.BaseName, item.HasFacts, item.Collection.Kind, item.Collection.Value)),
            actual.Classes.Select(item => (item.FullName, item.ShortName, item.Abstract,
                item.BaseName, item.HasFacts, item.Collection.Kind, item.Collection.Value)));
        if (expected.Ancestry is null) { Assert.Null(actual.Ancestry); return; }
        var ancestry = Assert.IsType<AcceptanceTestClassSourceScannerFactAncestry.AncestryFile>(actual.Ancestry);
        Assert.Equal(expected.Ancestry.GlobalUsings, ancestry.GlobalUsings);
        Assert.Equal(expected.Ancestry.Classes.Count, ancestry.Classes.Count);
        foreach (var (left, right) in expected.Ancestry.Classes.Zip(ancestry.Classes))
        {
            Assert.Equal((left.FullName, left.LookupName, left.BaseName), (right.FullName, right.LookupName, right.BaseName));
            Assert.Equal(left.Attributes, right.Attributes);
            Assert.Equal(left.Scopes.Count, right.Scopes.Count);
            foreach (var (leftScope, rightScope) in left.Scopes.Zip(right.Scopes))
            {
                Assert.Equal(leftScope.Prefix, rightScope.Prefix);
                Assert.Equal(leftScope.Usings, rightScope.Usings);
            }
        }
    }

    private sealed class StoreTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "source-extract-store-" + Guid.NewGuid().ToString("N"));
        internal StoreTree() => Directory.CreateDirectory(Root);
        internal string PathOf(string relative) => Path.Combine(Root, relative);
        internal string Write(string relative, string text)
        {
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
