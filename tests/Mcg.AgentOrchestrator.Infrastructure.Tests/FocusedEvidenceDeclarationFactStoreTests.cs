using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    [Theory]
    [InlineData("version")]
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
            tree.Write("Editable.cs", "namespace Sample; record AfterEditing { [Theory] void NewTestName() {} }");
            File.SetLastWriteTimeUtc(edited, oldTimestamp);
            Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(edited));
            Assert.NotEqual(oldLength, new FileInfo(edited).Length);

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
        Assert.Equal([source], tree.Find("AfterEditing"));
        Assert.Empty(FocusedEvidenceMethodResolver.FindTestMethodNames([source], "Before", tree.Root));
        Assert.Equal(["NewTestName"], FocusedEvidenceMethodResolver.FindTestMethodNames([source], "AfterEditing", tree.Root));
        Assert.Equal(new FocusedEvidenceParsedRootCache.DeclaredTypeName("AfterEditing", "Sample.AfterEditing"),
            Assert.Single(FocusedEvidenceBareSelectorResolution.ReadDeclaredClasses(source, tree.Root)));
    }

    private static string InvalidEntryJson(string hash, string defect)
    {
        var type = new JsonObject
        {
            ["SimpleName"] = "Decoy",
            ["QualifiedName"] = "Sample.Decoy",
            ["IsClassOrNonStructRecord"] = true,
            ["TestMethodNames"] = new JsonArray(JsonValue.Create("Wrong"))
        };
        var types = new JsonArray(type);
        var facts = new JsonObject { ["Types"] = types };
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
            case "hash": envelope["ContentHash"] = "stale"; break;
            case "facts-null": envelope["Facts"] = null; break;
            case "types-null": facts["Types"] = null; break;
            case "element-null": types[0] = null; break;
            case "simple-name-null": type["SimpleName"] = null; break;
            case "qualified-name-null": type["QualifiedName"] = null; break;
            case "classification-missing": type.Remove("IsClassOrNonStructRecord"); break;
            case "methods-null": type["TestMethodNames"] = null; break;
            case "method-null": type["TestMethodNames"]![0] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, "Unknown cache defect.");
        }
        return envelope.ToJsonString();
    }

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
