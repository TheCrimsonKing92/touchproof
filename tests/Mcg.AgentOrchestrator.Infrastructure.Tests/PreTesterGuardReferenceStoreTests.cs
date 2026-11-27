using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using GuardReference = Mcg.AgentOrchestrator.App.Orchestration.PreTesterGuardReferenceReader.GuardReference;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: each test owns a temporary directory and only reads repository source.
public sealed class PreTesterGuardReferenceStoreTests
{
    private const string GuardSource = """
        class GuardFixture
        {
            [Fact]
            void Guards()
            {
                File.ReadAllText(Path.Combine("src", "A.cs"));
                Directory.GetFiles("src", "*.cs", SearchOption.AllDirectories);
            }
        }
        """;

    [Fact]
    public void Read_SameDecodedTextAtDifferentPaths_ParsesOnce()
    {
        using var tree = new StoreTree();
        var first = tree.WriteSource("first.cs", GuardSource);
        var second = Path.Combine(tree.Root, "second.cs");
        File.WriteAllText(second, GuardSource, Encoding.Unicode);

        AssertReadParsesOnce(tree, first, second,
        [
            new("GuardFixture", "read", ["src", "A.cs"]),
            new("GuardFixture", "scan", ["src"], "*.cs", true)
        ]);
    }

    [Fact]
    public void Read_RepositoryGuardTestSource_ParsesOnceAndRoundTripsReferences()
    {
        using var tree = new StoreTree();
        var file = Path.Combine(VerifiedRepositoryRoot.Find(), "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests", "ProcessTreeGuiSuppressionTests.cs");
        var parsed = PreTesterGuardReferenceReader.Parse(File.ReadAllText(file));
        Assert.NotEmpty(parsed);

        AssertReadParsesOnce(tree, file, file, parsed);
    }

    [Fact]
    public void Store_GuardClassSelectorSource_RoundTripsParsedResult()
    {
        using var tree = new StoreTree();
        var file = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App",
            "Orchestration", "PreTesterChangedFileGuardClasses.cs");
        var source = File.ReadAllText(file);
        var parsed = PreTesterGuardReferenceReader.Parse(source);
        Assert.Empty(parsed);
        Assert.Empty(PreTesterGuardReferenceReader.Read(file,
            _ => throw new InvalidOperationException("Quick rejection must precede store resolution."),
            _ => throw new InvalidOperationException("Quick rejection must precede parsing.")));
        Assert.False(Directory.Exists(tree.CacheDirectory));

        var hash = AcceptanceTestClassSourceExtractStore.HashText(source);
        tree.NewStore().Write(hash, parsed);
        Assert.True(tree.NewStore().TryRead(hash, out var cached));
        AssertReferencesEqual(parsed, cached);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("non-json")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("hash")]
    [InlineData("references-null")]
    [InlineData("element-null")]
    [InlineData("class-null")]
    [InlineData("kind-null")]
    [InlineData("pattern-null")]
    [InlineData("path-null")]
    [InlineData("path-segment-null")]
    [InlineData("recursive-wrong-type")]
    public void Read_InvalidEntry_ParsesAndRepairs(string defect)
    {
        using var tree = new StoreTree();
        var file = tree.WriteSource("guard.cs", GuardSource);
        var hash = AcceptanceTestClassSourceExtractStore.HashText(GuardSource);
        var store = tree.NewStore();
        var expected = PreTesterGuardReferenceReader.Read(file, _ => store, PreTesterGuardReferenceReader.Parse);
        Assert.NotEmpty(expected);
        Assert.True(store.TryRead(hash, out _));
        var entryPath = store.EntryPath(hash);
        File.WriteAllText(entryPath, CorruptEntry(File.ReadAllText(entryPath), defect));

        var parseCount = 0;
        var repaired = PreTesterGuardReferenceReader.Read(file, _ => tree.NewStore(), source =>
        {
            parseCount++;
            return PreTesterGuardReferenceReader.Parse(source);
        });

        Assert.Equal(1, parseCount);
        AssertReferencesEqual(expected, repaired);
        Assert.True(tree.NewStore().TryRead(hash, out var cached));
        AssertReferencesEqual(expected, cached);
    }

    [Fact]
    public void Read_TokenFreeSource_SkipsResolutionParsingAndPersistence()
    {
        using var tree = new StoreTree();
        var file = tree.WriteSource("plain.cs", "class Plain {}");
        var resolutionCount = 0;
        var parseCount = 0;

        var references = PreTesterGuardReferenceReader.Read(file, _ =>
        {
            resolutionCount++;
            return tree.NewStore();
        }, source =>
        {
            parseCount++;
            return PreTesterGuardReferenceReader.Parse(source);
        });

        Assert.Empty(references);
        Assert.Equal(0, resolutionCount);
        Assert.Equal(0, parseCount);
        Assert.False(Directory.Exists(tree.CacheDirectory));
    }

    [Fact]
    public void Read_EligibleSourceWithNoReferences_PersistsEmptyResult()
    {
        using var tree = new StoreTree();
        var file = tree.WriteSource("no-tests.cs", "// ReadAllText\nclass Plain {}");

        AssertReadParsesOnce(tree, file, file, []);
    }

    [Fact]
    public void Read_WithoutOwnerState_ParsesWithoutCreatingDirectories()
    {
        using var tree = new StoreTree();
        var gitDirectory = Directory.CreateDirectory(Path.Combine(tree.Root, ".git")).FullName;
        var file = tree.WriteSource("guard.cs", GuardSource);
        var expected = PreTesterGuardReferenceReader.Parse(GuardSource);

        Assert.NotEmpty(expected);
        Assert.Null(PreTesterGuardReferenceStore.ResolveForFile(file));
        AssertReferencesEqual(expected, PreTesterGuardReferenceReader.Read(file));
        Assert.Equal(gitDirectory, Assert.Single(Directory.GetDirectories(tree.Root, "*", SearchOption.AllDirectories)));
    }

    [Fact]
    public void Read_GoalWorktree_WritesInOwnerState()
    {
        using var tree = new StoreTree();
        var ownerState = Directory.CreateDirectory(Path.Combine(tree.Root, ".orchestrator")).FullName;
        const string worktree = ".orchestrator-worktrees/goal";
        tree.WriteSource(worktree + "/.git", "gitdir: marker");
        var file = tree.WriteSource(worktree + "/tests/guard.cs", GuardSource);
        var hash = AcceptanceTestClassSourceExtractStore.HashText(GuardSource);
        var expectedEntryPath = Path.Combine(ownerState, PreTesterGuardReferenceStore.DirectoryName,
            PreTesterGuardReferenceStore.ReaderVersion.ToString(CultureInfo.InvariantCulture), hash + ".json");

        var store = Assert.IsType<PreTesterGuardReferenceStore>(PreTesterGuardReferenceStore.ResolveForFile(file));
        Assert.Equal(expectedEntryPath, store.EntryPath(hash));
        var parsed = PreTesterGuardReferenceReader.Read(file);
        Assert.NotEmpty(parsed);
        Assert.Equal(expectedEntryPath, Assert.Single(Directory.GetFiles(ownerState, "*", SearchOption.AllDirectories)));
        Assert.True(store.TryRead(hash, out var cached));
        AssertReferencesEqual(parsed, cached);
    }

    [Fact]
    public void Read_WhenPublicationFails_ReturnsParsedReferencesAndRemovesTemporaryFile()
    {
        using var tree = new StoreTree();
        var file = tree.WriteSource("guard.cs", GuardSource);
        var store = tree.NewStore();
        var entryPath = store.EntryPath(AcceptanceTestClassSourceExtractStore.HashText(GuardSource));
        // A directory at the final path lets the temporary write succeed and blocks the move.
        Directory.CreateDirectory(entryPath);

        var parsed = PreTesterGuardReferenceReader.Read(file, _ => store, PreTesterGuardReferenceReader.Parse);

        Assert.NotEmpty(parsed);
        AssertReferencesEqual(PreTesterGuardReferenceReader.Parse(GuardSource), parsed);
        Assert.True(Directory.Exists(entryPath));
        Assert.Empty(Directory.GetFiles(tree.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Registry_DeclaresCacheWithWriterEvidence()
    {
        var entry = Assert.IsType<OrchestratorArtifactRegistry.Entry>(
            OrchestratorArtifactRegistry.Classify(PreTesterGuardReferenceStore.DirectoryName));

        Assert.Equal(PreTesterGuardReferenceStore.DirectoryName, entry.Pattern);
        Assert.Equal(nameof(PreTesterGuardReferenceStore), entry.Owner);
        Assert.Equal(OrchestratorArtifactRegistry.WriterKind.Source, entry.Writer);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.Unmanaged, entry.Retention);
        Assert.True(entry.BudgetBytes > 0);
        Assert.Equal("src/Mcg.AgentOrchestrator.App/Orchestration/PreTesterGuardReferenceStore.cs", entry.WriterPath);
        var writer = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), entry.WriterPath!));
        Assert.Contains(entry.WriterLiteral, writer, StringComparison.Ordinal);
    }

    private static void AssertReadParsesOnce(StoreTree tree, string first, string second, GuardReference[] expected)
    {
        var parseCount = 0;
        GuardReference[] Parse(string source)
        {
            parseCount++;
            return PreTesterGuardReferenceReader.Parse(source);
        }

        var parsed = PreTesterGuardReferenceReader.Read(first, _ => tree.NewStore(), Parse);
        Assert.Equal(1, parseCount);
        var cached = PreTesterGuardReferenceReader.Read(second, _ => tree.NewStore(), Parse);
        Assert.Equal(1, parseCount);
        AssertReferencesEqual(expected, parsed);
        AssertReferencesEqual(parsed, cached);
        Assert.Single(Directory.GetFiles(tree.CacheDirectory, "*", SearchOption.AllDirectories));
    }

    private static string CorruptEntry(string json, string defect)
    {
        if (defect == "truncated") return json[..^1];
        if (defect == "non-json") return "not JSON";
        if (defect == "null") return "null";

        var entry = JsonNode.Parse(json)!;
        var references = entry["References"]!;
        var first = references[0]!;
        switch (defect)
        {
            case "version": entry["ReaderVersion"] = PreTesterGuardReferenceStore.ReaderVersion + 1; break;
            case "hash": entry["ContentHash"] = "foreign"; break;
            case "references-null": entry["References"] = null; break;
            case "element-null": references[0] = null; break;
            case "class-null": first["Class"] = null; break;
            case "kind-null": first["Kind"] = null; break;
            case "pattern-null": first["Pattern"] = null; break;
            case "path-null": first["Path"] = null; break;
            case "path-segment-null": first["Path"]![0] = null; break;
            case "recursive-wrong-type": first["Recursive"] = "true"; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, null);
        }
        return entry.ToJsonString();
    }

    private static void AssertReferencesEqual(GuardReference[] expected, GuardReference[] actual) =>
        Assert.Equal(expected, actual, (left, right) =>
            left.Class == right.Class && left.Kind == right.Kind && left.Pattern == right.Pattern &&
            left.Recursive == right.Recursive && left.Path.SequenceEqual(right.Path));

    private sealed class StoreTree : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("mcg-guard-ref-").FullName;
        internal string CacheDirectory => Path.Combine(Root, PreTesterGuardReferenceStore.DirectoryName);
        internal PreTesterGuardReferenceStore NewStore() => new(CacheDirectory);

        internal string WriteSource(string relativePath, string source)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
