using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

public sealed class FocusedEvidenceDeclarationFactStoreTests
{
    [Fact]
    public void ColdLookupsMatchDirectParsing()
    {
        using var tree = new SourceTree(ownerState: true);
        tree.Populate();
        using var store = UseColdStore();

        AssertLookupsMatchDirectParsing(tree);
        Assert.All(tree.Files(), path => Assert.True(File.Exists(tree.EntryFor(path)), path));
    }

    [Fact]
    public void PersistedFactsServeAllLookupsWithColdStoresAndZeroParses()
    {
        using var tree = new SourceTree(ownerState: true);
        tree.Populate();
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        using (UseColdStore())
            AssertLookupsMatchDirectParsing(tree);
        Assert.All(tree.Files(), path =>
        {
            Assert.Equal(1, parses.CountFor(path));
            Assert.True(File.Exists(tree.EntryFor(path)), path);
        });

        // RecordParse updates ParseCount and this tree's observation together.
        // The observation excludes concurrent parses of other tests' files.
        var beforeSecondPass = parses.Total;
        using (UseColdStore())
            AssertLookupsMatchDirectParsing(tree);
        Assert.Equal(beforeSecondPass, parses.Total);
    }

    [Fact]
    public void PersistedFactsServeTraitExclusionDiscoveryWithColdStoresAndZeroParses()
    {
        using var tree = new SourceTree(ownerState: true);
        tree.Write("Host.cs", """
            class HostTests
            {
                [Xunit.Fact, Xunit.Trait("Category","HostIntegration")] void A() {}
                [OptInFact, Xunit.Trait("Category","HostIntegration")] void B() {}
            }
            """);
        tree.Write("Attributes.cs", "class OptInFactAttribute : Xunit.FactAttribute {}");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        var files = tree.Files();
        Assert.Equal(2, files.Length);
        using (UseColdStore())
        {
            foreach (var path in files)
                FocusedEvidenceDeclarationFactStore.Current.Read(path, tree.Root);
        }
        Assert.All(files, path =>
        {
            Assert.Equal(1, parses.CountFor(path));
            Assert.True(File.Exists(tree.EntryFor(path)), path);
        });

        // RecordParse updates the process ParseCount and this tree's observation together.
        // The tree-scoped count excludes unrelated parallel test parses.
        var beforeDiscovery = parses.Total;
        using (UseColdStore())
            Assert.Equal("excluded-by-trait:Category=HostIntegration", Assert.Single(ProveHostExcluded(tree)!));
        Assert.Equal(beforeDiscovery, parses.Total);
    }

    [Fact]
    public void MissingUsingNameIsRepairedAndExplicitNullIsReusable()
    {
        const string reason = "excluded-by-trait:Category=HostIntegration";
        using var tree = new SourceTree(ownerState: true);
        var source = tree.Write("Host.cs", """
            using Xunit;
            class HostTests { [Fact, Trait("Category","HostIntegration")] void A() {} }
            """);
        var aliasSource = tree.Write("Alias.cs", "using Alias = int[];");
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        using (UseColdStore())
            Assert.Equal(reason, Assert.Single(ProveHostExcluded(tree)!));

        var aliasEntry = JsonNode.Parse(File.ReadAllText(tree.EntryFor(aliasSource)))!;
        var aliasUsing = aliasEntry["Facts"]!["Usings"]![0]!.AsObject();
        Assert.True(aliasUsing.TryGetPropertyValue("Name", out var aliasName));
        Assert.Null(aliasName);

        var entryPath = tree.EntryFor(source);
        var entry = JsonNode.Parse(File.ReadAllText(entryPath))!;
        Assert.True(entry["Facts"]!["Usings"]![0]!.AsObject().Remove("Name"));
        File.WriteAllText(entryPath, entry.ToJsonString());

        using (UseColdStore())
            Assert.Equal(reason, Assert.Single(ProveHostExcluded(tree)!));
        Assert.Equal(2, parses.CountFor(source));
        Assert.Equal(1, parses.CountFor(aliasSource));
        var repaired = JsonNode.Parse(File.ReadAllText(entryPath))!;
        Assert.Equal("Xunit", repaired["Facts"]!["Usings"]![0]!["Name"]!.GetValue<string>());

        var beforeReuse = parses.Total;
        using (UseColdStore())
            Assert.Equal(reason, Assert.Single(ProveHostExcluded(tree)!));
        Assert.Equal(beforeReuse, parses.Total);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("truncated")]
    [InlineData("shape-null")]
    [InlineData("attribute-name-null")]
    [InlineData("older-version")]
    [InlineData("legacy-v2-shape")]
    public void InvalidEntryIsRebuiltWithSameTraitExclusionDiscovery(string defect)
    {
        const string eligibleSource = """
            class HostTests { [Xunit.Fact, Xunit.Trait("Category","HostIntegration")] void A() {} }
            """;
        var currentSource = eligibleSource.Replace("HostIntegration", "HostIntegratioN", StringComparison.Ordinal);
        using var reference = new SourceTree(ownerState: false);
        reference.Write("Host.cs", currentSource);
        IReadOnlyList<string>? expected;
        using (UseColdStore())
            expected = ProveHostExcluded(reference);
        Assert.Null(expected);

        using var tree = new SourceTree(ownerState: true);
        var source = tree.Write("Host.cs", eligibleSource);
        var oldEntry = tree.EntryFor(source);
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        using var warmStore = UseColdStore();
        Assert.Equal("excluded-by-trait:Category=HostIntegration", Assert.Single(ProveHostExcluded(tree)!));
        Assert.Equal(1, parses.CountFor(source));
        var oldTimestamp = File.GetLastWriteTimeUtc(source);
        var oldLength = new FileInfo(source).Length;
        tree.Write("Host.cs", currentSource);
        File.SetLastWriteTimeUtc(source, oldTimestamp);
        Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(source));
        Assert.Equal(oldLength, new FileInfo(source).Length);
        var currentEntry = tree.EntryFor(source);
        Assert.NotEqual(oldEntry, currentEntry);
        Assert.False(File.Exists(currentEntry));

        if (defect == "edited")
        {
            Assert.Equal(expected, ProveHostExcluded(tree));
        }
        else
        {
            var decoy = JsonNode.Parse(File.ReadAllText(oldEntry))!.AsObject();
            decoy["ContentHash"] = tree.Hash(source);
            File.WriteAllText(currentEntry, DefectiveTraitEntry(decoy, defect));
            using (UseColdStore())
                Assert.Equal(expected, ProveHostExcluded(tree));
        }

        Assert.Equal(2, parses.CountFor(source));
        Assert.True(File.Exists(oldEntry));
        var repaired = JsonNode.Parse(File.ReadAllText(currentEntry))!;
        Assert.Equal(FocusedEvidenceDeclarationFactStore.ExtractorVersion, repaired["ExtractorVersion"]!.GetValue<int>());
        Assert.Equal(tree.Hash(source), repaired["ContentHash"]!.GetValue<string>());
        Assert.NotNull(repaired["Facts"]!["Types"]![0]!["Shape"]);
        using (UseColdStore())
            Assert.Equal(expected, ProveHostExcluded(tree));
        Assert.Equal(2, parses.CountFor(source));
    }

    [Fact]
    public void ExtractsExplicitModifiersRecordKindAndErrorSeverity()
    {
        using var tree = new SourceTree(ownerState: true);
        var path = tree.Write("Shapes.cs", """
            #warning Fixture warning
            public static class PublicStatic { }
            internal class Internal { }
            public abstract class PublicAbstract { }
            public record PublicRecord;
            """);
        var invalidPath = tree.Write("Malformed.cs", "public class Malformed {");
        using var store = UseColdStore();

        var facts = FocusedEvidenceDeclarationFactStore.Current.Read(path, tree.Root);
        var shapes = facts.Types.ToDictionary(type => type.SimpleName, type => type.Shape);
        Assert.True(facts.ContainsDiagnostics);
        Assert.False(facts.ContainsErrorDiagnostics);
        Assert.True(FocusedEvidenceDeclarationFactStore.Current.Read(invalidPath, tree.Root).ContainsErrorDiagnostics);
        Assert.True(shapes["PublicStatic"].IsPublic);
        Assert.True(shapes["PublicStatic"].IsStatic);
        Assert.False(shapes["PublicStatic"].IsRecord);
        Assert.False(shapes["Internal"].IsPublic);
        Assert.False(shapes["Internal"].IsStatic);
        Assert.False(shapes["Internal"].IsRecord);
        Assert.True(shapes["PublicAbstract"].IsPublic);
        Assert.False(shapes["PublicAbstract"].IsStatic);
        Assert.True(shapes["PublicRecord"].IsPublic);
        Assert.False(shapes["PublicRecord"].IsStatic);
        Assert.True(shapes["PublicRecord"].IsRecord);
        Assert.Equal(4, FocusedEvidenceDeclarationFactStore.ExtractorVersion);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("older-version")]
    [InlineData("hash")]
    [InlineData("truncated")]
    [InlineData("non-json")]
    [InlineData("null")]
    [InlineData("facts-null")]
    [InlineData("types-null")]
    [InlineData("element-null")]
    [InlineData("simple-name-null")]
    [InlineData("qualified-name-null")]
    [InlineData("classification-missing")]
    [InlineData("methods-null")]
    [InlineData("method-null")]
    [InlineData("diagnostics-missing")]
    [InlineData("error-diagnostics-missing")]
    [InlineData("public-missing")]
    [InlineData("static-missing")]
    [InlineData("record-missing")]
    [InlineData("directives-missing")]
    [InlineData("base-list-missing")]
    [InlineData("attributes-null")]
    [InlineData("attribute-null")]
    public void InvalidEntryFallsBackToCurrentSourceAndRepairsEntry(string defect)
    {
        using var tree = new SourceTree(ownerState: true);
        var source = tree.Write("Current.cs", "namespace Sample; class Current { [Fact] void Right() {} }");
        var entryPath = tree.EntryFor(source);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllText(entryPath, InvalidEntryJson(tree.Hash(source), defect));
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);

        using (UseColdStore())
            AssertCurrentSource(tree, source);
        Assert.Equal(1, parses.CountFor(source));

        using (UseColdStore())
            AssertCurrentSource(tree, source);
        Assert.Equal(1, parses.CountFor(source));
    }

    [Theory]
    [InlineData("older-version")]
    [InlineData("hash")]
    [InlineData("truncated")]
    public void InvalidEntryIsRebuiltWithSameTestlessSelection(string defect)
    {
        using var tree = new SourceTree(ownerState: true);
        var source = tree.Write("Current.cs", "namespace Sample; class SupportHelper { [Fact] void Runs() {} }");
        const string selector = "SupportHelper";
        var token = new FocusedEvidenceFilterToken(selector, selector, FocusedEvidenceTokenKind.Class, selector, selector);
        var filter = new FocusedEvidenceFilter(selector, selector, [token]);
        AssertSelection();

        // The decoy has a recognised test elsewhere and would prove SupportHelper testless if accepted.
        File.WriteAllText(tree.EntryFor(source), InvalidEntryJson(tree.Hash(source), defect, testlessSelector: selector));
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);
        AssertSelection();
        Assert.Equal(1, parses.CountFor(source));
        AssertSelection();
        Assert.Equal(1, parses.CountFor(source));

        void AssertSelection()
        {
            using var store = UseColdStore();
            var result = FocusedEvidenceTestlessClassSelection.Apply(tree.Root,
                [("target", SourceTree.Project, filter)], [], out var dropped);
            Assert.Equal(("target", SourceTree.Project, filter), Assert.Single(result));
            Assert.Empty(dropped);
        }
    }

    [Fact]
    public void EditedSourceInvalidatesFactsInWarmAndFreshStores()
    {
        using var tree = new SourceTree(ownerState: true);
        var edited = tree.Write("Editable.cs", "namespace Sample; class Before { [Fact] void Old() {} }");
        var unchanged = tree.Write("Unchanged.cs", "namespace Sample; record Stable;");
        var oldEntry = tree.EntryFor(edited);
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);

        using (UseColdStore())
        {
            Assert.Equal([edited], tree.Find("Before"));
            Assert.Equal(["Old"], FocusedEvidenceMethodResolver.FindTestMethodNames([edited], "Before", tree.Root));

            var oldTimestamp = File.GetLastWriteTimeUtc(edited);
            var oldLength = new FileInfo(edited).Length;
            tree.Write("Editable.cs", "namespace Sample; record After { [Fact] void New() {} }");
            File.SetLastWriteTimeUtc(edited, oldTimestamp);
            Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(edited));
            Assert.Equal(oldLength, new FileInfo(edited).Length);

            AssertEditedSource(tree, edited);
        }
        Assert.True(File.Exists(oldEntry));
        Assert.True(File.Exists(tree.EntryFor(edited)));
        Assert.Equal(2, parses.CountFor(edited));
        Assert.Equal(1, parses.CountFor(unchanged));

        using (UseColdStore())
            AssertEditedSource(tree, edited);
        Assert.Equal(2, parses.CountFor(edited));
        Assert.Equal(1, parses.CountFor(unchanged));
    }

    [Fact]
    public void MissingOwnerStateLeavesNoPersistedFacts()
    {
        using var tree = new SourceTree(ownerState: false);
        tree.Populate();
        using var parses = FocusedEvidenceParsedRootCache.Observe(tree.Root);

        using (UseColdStore())
            AssertLookupsMatchDirectParsing(tree);
        using (UseColdStore())
            AssertLookupsMatchDirectParsing(tree);

        Assert.All(tree.Files(), path => Assert.Equal(2, parses.CountFor(path)));
        Assert.Empty(Directory.EnumerateDirectories(tree.Root, ".orchestrator", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(tree.Root, FocusedEvidenceDeclarationFactStore.DirectoryName,
            SearchOption.AllDirectories));
    }

    private static IDisposable UseColdStore() => FocusedEvidenceDeclarationFactStore.Use(
        new FocusedEvidenceDeclarationFactStore(new FocusedEvidenceParsedRootCache.Store(32)));

    private static IReadOnlyList<string>? ProveHostExcluded(SourceTree tree)
    {
        var discovery = FocusedEvidenceTraitExclusionDiscovery.Read(tree.Root, SourceTree.Project);
        Assert.NotNull(discovery);
        Assert.True(discovery.HasMatchingDeclaration("HostTests"));
        return discovery.ProveExcluded("HostTests", [], [("Category", "HostIntegration")]);
    }

    private static string DefectiveTraitEntry(JsonObject envelope, string defect)
    {
        var facts = envelope["Facts"]!.AsObject();
        var type = facts["Types"]![0]!.AsObject();
        switch (defect)
        {
            case "truncated": return envelope.ToJsonString()[..^1];
            case "shape-null": type["Shape"] = null; break;
            case "attribute-name-null": type["Shape"]!["Methods"]![0]!["Attributes"]![0]!["Name"] = null; break;
            case "legacy-v2-shape":
                type.Remove("Shape");
                facts.Remove("Usings");
                goto case "older-version";
            case "older-version":
                envelope["ExtractorVersion"] = FocusedEvidenceDeclarationFactStore.ExtractorVersion - 1;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown cache defect.");
        }
        return envelope.ToJsonString();
    }

    private static void AssertLookupsMatchDirectParsing(SourceTree tree)
    {
        var files = tree.Files();
        Assert.Equal(3, files.Length);
        var typesByFile = files.ToDictionary(path => path, path => CSharpSyntaxTree.ParseText(File.ReadAllText(path))
            .GetCompilationUnitRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().ToArray());
        foreach (var selector in new[] { "Shared", "Sample.Shared", "Shared.Inner", "Sam.RecordClass", "RecordClass",
                     "PlainRecord", "ValueRecord", "Value", "Contract", "FixtureOnly", "Ignored", "Missing" })
        {
            var expectedFiles = files.Where(path => typesByFile[path].Any(type =>
                FocusedEvidenceRequestResolver.FocusedEvidenceTypeMatches(type, selector))).ToArray();
            var expectedMethods = expectedFiles.SelectMany(path => typesByFile[path])
                .Where(type => FocusedEvidenceRequestResolver.FocusedEvidenceTypeMatches(type, selector))
                .SelectMany(type => type.Members.OfType<MethodDeclarationSyntax>())
                .Where(method => method.AttributeLists.SelectMany(list => list.Attributes)
                    .Any(attribute => FocusedEvidenceMethodResolver.IsTestAttribute(attribute.Name.ToString())))
                .Select(method => method.Identifier.ValueText).ToArray();
            // Use the file-only path before class discovery warms the fact store.
            Assert.Equal(expectedMethods, FocusedEvidenceMethodResolver.FindTestMethodNames(expectedFiles, selector));
            Assert.Equal(expectedFiles, tree.Find(selector));
        }

        foreach (var path in files)
        {
            var expectedClasses = typesByFile[path].Where(type => type is ClassDeclarationSyntax ||
                    type is RecordDeclarationSyntax record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
                .Select(FocusedEvidenceParsedRootCache.GetDeclaredTypeName).ToArray();
            Assert.Equal(expectedClasses, FocusedEvidenceBareSelectorResolution.ReadDeclaredClasses(path, tree.Root));
        }

        Assert.Equal(["Shared", "Shared.Inner", "ValueRecord", "Missing"],
            FocusedEvidenceNestedProjectClassSelection.ExcludeFixtureClasses(tree.Root, SourceTree.Project,
                ["Shared", "Shared.Inner", "FixtureOnly", "ValueRecord", "Missing"]).ToArray());
    }

    private static void AssertCurrentSource(SourceTree tree, string source)
    {
        Assert.Equal([source], tree.Find("Current"));
        Assert.Empty(tree.Find("Decoy"));
        Assert.Equal(["Right"], FocusedEvidenceMethodResolver.FindTestMethodNames([source], "Current", tree.Root));
        Assert.Equal(new FocusedEvidenceParsedRootCache.DeclaredTypeName("Current", "Sample.Current"),
            Assert.Single(FocusedEvidenceBareSelectorResolution.ReadDeclaredClasses(source, tree.Root)));
    }

    private static void AssertEditedSource(SourceTree tree, string source)
    {
        Assert.Empty(tree.Find("Before"));
        Assert.Equal([source], tree.Find("After"));
        Assert.Empty(FocusedEvidenceMethodResolver.FindTestMethodNames([source], "Before", tree.Root));
        Assert.Equal(["New"], FocusedEvidenceMethodResolver.FindTestMethodNames([source], "After", tree.Root));
        Assert.Equal(new FocusedEvidenceParsedRootCache.DeclaredTypeName("After", "Sample.After"),
            Assert.Single(FocusedEvidenceBareSelectorResolution.ReadDeclaredClasses(source, tree.Root)));
    }

    private static string InvalidEntryJson(string hash, string defect, string? testlessSelector = null)
    {
        var type = new JsonObject
        {
            ["SimpleName"] = "Decoy",
            ["QualifiedName"] = "Sample.Decoy",
            ["IsClassOrNonStructRecord"] = true,
            ["TestMethodNames"] = new JsonArray(JsonValue.Create("Wrong")),
            ["HasBaseList"] = false,
            ["MethodAttributeNames"] = new JsonArray(JsonValue.Create("Fact")),
            ["Shape"] = EmptyTypeShape()
        };
        var types = new JsonArray(type);
        if (testlessSelector is not null)
            types.Add(new JsonObject
            {
                ["SimpleName"] = testlessSelector,
                ["QualifiedName"] = "Sample." + testlessSelector,
                ["IsClassOrNonStructRecord"] = true,
                ["TestMethodNames"] = new JsonArray(),
                ["HasBaseList"] = false,
                ["MethodAttributeNames"] = new JsonArray(),
                ["Shape"] = EmptyTypeShape()
            });
        var facts = new JsonObject
        {
            ["Types"] = types,
            ["ContainsDiagnostics"] = false,
            ["ContainsErrorDiagnostics"] = false,
            ["ContainsDirectives"] = false,
            ["Usings"] = new JsonArray()
        };
        var envelope = new JsonObject
        {
            ["ExtractorVersion"] = FocusedEvidenceDeclarationFactStore.ExtractorVersion,
            ["ContentHash"] = hash,
            ["Facts"] = facts
        };
        switch (defect)
        {
            case "truncated": return envelope.ToJsonString()[..^1];
            case "non-json": return "not JSON";
            case "null": return "null";
            case "version": envelope["ExtractorVersion"] = FocusedEvidenceDeclarationFactStore.ExtractorVersion + 1; break;
            case "older-version": envelope["ExtractorVersion"] = FocusedEvidenceDeclarationFactStore.ExtractorVersion - 1; break;
            case "hash": envelope["ContentHash"] = "stale"; break;
            case "facts-null": envelope["Facts"] = null; break;
            case "types-null": facts["Types"] = null; break;
            case "element-null": types[0] = null; break;
            case "simple-name-null": type["SimpleName"] = null; break;
            case "qualified-name-null": type["QualifiedName"] = null; break;
            case "classification-missing": type.Remove("IsClassOrNonStructRecord"); break;
            case "methods-null": type["TestMethodNames"] = null; break;
            case "method-null": type["TestMethodNames"]![0] = null; break;
            case "diagnostics-missing": facts.Remove("ContainsDiagnostics"); break;
            case "error-diagnostics-missing": facts.Remove("ContainsErrorDiagnostics"); break;
            case "public-missing": type["Shape"]!.AsObject().Remove("IsPublic"); break;
            case "static-missing": type["Shape"]!.AsObject().Remove("IsStatic"); break;
            case "record-missing": type["Shape"]!.AsObject().Remove("IsRecord"); break;
            case "directives-missing": facts.Remove("ContainsDirectives"); break;
            case "base-list-missing": type.Remove("HasBaseList"); break;
            case "attributes-null": type["MethodAttributeNames"] = null; break;
            case "attribute-null": type["MethodAttributeNames"]![0] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown cache defect.");
        }
        return envelope.ToJsonString();
    }

    private static JsonObject EmptyTypeShape() => new()
    {
        ["IsAbstract"] = false,
        ["IsPublic"] = false,
        ["IsStatic"] = false,
        ["IsRecord"] = false,
        ["HasTypeParameters"] = false,
        ["IsStructOrInterface"] = false,
        ["IsNested"] = false,
        ["BaseTypeNames"] = new JsonArray(),
        ["Attributes"] = new JsonArray(),
        ["Methods"] = new JsonArray()
    };

    private sealed class SourceTree : IDisposable
    {
        internal const string Project = "tests/P/P.csproj";
        private readonly string _marker = "\n// fixture " + Guid.NewGuid().ToString("N");
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();

        internal SourceTree(bool ownerState)
        {
            File.WriteAllText(Path.Combine(Root, ".git"), "gitdir: fixture");
            if (ownerState) Directory.CreateDirectory(Path.Combine(Root, ".orchestrator"));
        }

        internal void Populate()
        {
            Write("First.cs", """
                namespace Sample;
                partial class Shared<T>
                {
                    [Fact] void Repeated() {}
                    [Xunit.TheoryAttribute] void Repeated(int arg) {}
                    [Other] void Helper() {}
                    class Inner { [Theory] void Nested() {} }
                }
                record PlainRecord { [Xunit.Fact] void Plain() {} }
                record struct ValueRecord { [FactAttribute] void RecordStructTest() {} }
                """);
            Write("Second.cs", """
                namespace Sample
                {
                    partial class Shared<T> { [Theory] void OtherFile() {} }
                    interface Contract { [Fact] void InterfaceTest(); }
                    struct Value { [Fact] void StructTest() {} }
                }
                namespace Sam
                {
                    record class RecordClass { [Xunit.FactAttribute] void RecordClassTest() {} }
                    enum Ignored { Value }
                }
                """);
            Write("Fixtures/F.csproj", "<Project />");
            Write("Fixtures/Fixture.cs", "namespace Sample; class FixtureOnly { [Fact] void FixtureTest() {} }");
        }

        internal string Write(string relativePath, string source)
        {
            var path = Path.Combine(Root, "tests", "P", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetExtension(path) == ".cs" ? source + _marker : source);
            return path;
        }

        internal string[] Files() =>
            FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(Root, Project).ToArray();

        internal IReadOnlyList<string> Find(string name) =>
            FocusedEvidenceRequestResolver.FindFocusedEvidenceClassFiles(Root, Project, name);

        internal string Hash(string path) => AcceptanceTestClassSourceExtractStore.HashText(File.ReadAllText(path));

        internal string EntryFor(string path) => FocusedEvidenceDeclarationFactStore.EntryPath(
            Path.Combine(Root, ".orchestrator", FocusedEvidenceDeclarationFactStore.DirectoryName), Hash(path));

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
