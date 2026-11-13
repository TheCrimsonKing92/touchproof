using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: every case owns small temporary sources and a per-call probe; no processes or global state.
public sealed class ImpactReplayChangedSupportReferenceScanTests
{
    [Theory]
    [InlineData("short")]
    [InlineData("alias")]
    [InlineData("global-alias")]
    [InlineData("namespace-alias")]
    [InlineData("constructor")]
    [InlineData("attribute")]
    [InlineData("generic")]
    [InlineData("escaped")]
    [InlineData("collision")]
    [InlineData("unreferenced")]
    [InlineData("ordered")]
    [InlineData("reversed")]
    public void ReferenceShapes_MatchLegacySelection(string shape)
    {
        using var sources = new SourceFixture();
        var helper = shape switch
        {
            "attribute" => "public class HelpersAttribute : System.Attribute { }",
            "generic" => "public class Helpers<T> { }",
            "constructor" => "public class Helpers { public Helpers(int value) { } }",
            _ => "public class Helpers { }"
        };
        var beta = shape switch
        {
            "short" => "using Support; namespace SampleTests; public class BetaTests { Helpers value; }",
            "alias" => "using H = Support.Helpers; namespace SampleTests; public class BetaTests { object Make() => new H(); }",
            "global-alias" => "global using H = Support.Helpers; namespace SampleTests; public class BetaTests { H value; }",
            "namespace-alias" => "namespace SampleTests { using H = Support.Helpers; public class BetaTests { H value; } }",
            "constructor" => "namespace SampleTests; public class BetaTests { object Make() => new Support.Helpers(1); }",
            "attribute" => "namespace SampleTests; [Support.Helpers] public class BetaTests { }",
            "generic" => "namespace SampleTests; public class BetaTests { Support.Helpers<int> value; }",
            "escaped" => "using Support; namespace SampleTests; public class BetaTests { @Helpers value; }",
            "collision" => "namespace SampleTests; public class BetaTests { Other.Helpers value; }",
            "unreferenced" => "namespace SampleTests; public class BetaTests { int value; }",
            _ => "namespace SampleTests; public class BetaTests { Support.Helpers value; }"
        };
        sources.Add("Support.cs", "namespace Support; " + helper);
        sources.Add("Unused.cs", "namespace Support; public class Unused { }");
        sources.Add("Other.cs", "namespace Other; public class Helpers { }");
        sources.Add("BetaTests.cs", beta);
        sources.Add("elsewhere/OtherTests.cs", beta.Replace("BetaTests", "OtherTests", StringComparison.Ordinal)
            .Replace("global using H = Support.Helpers;", "", StringComparison.Ordinal), infrastructure: false);
        string[] changed = shape switch
        {
            "ordered" => ["Support.cs", "Unused.cs"],
            "reversed" => ["Unused.cs", "Support.cs"],
            _ => ["Support.cs"]
        };
        var actual = sources.Select(changed);
        var legacy = LegacySelection(sources.Root, changed, sources.Projects, sources.Runnable);
        var abandon = shape switch
        {
            "collision" or "unreferenced" => "Changed test support has no resolvable test class: Support.cs",
            "ordered" or "reversed" => "Changed test support has no resolvable test class: Unused.cs",
            _ => null
        };
        Assert.Equal(abandon, actual.AbandonReason);
        Assert.Equal(abandon is null ? new[] { "SampleTests.BetaTests" } : [], actual.Classes);
        Assert.Equal(shape is "collision" or "unreferenced" or "reversed" ? [] : new[] { "elsewhere/OtherTests.cs" }, actual.OtherFiles);
        Assert.Equal(legacy.Classes, actual.Classes);
        Assert.Equal(legacy.AbandonReason, actual.AbandonReason);
        Assert.Equal(legacy.OtherFiles, actual.OtherFiles);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alias")]
    [InlineData("attribute")]
    public void NonMatchingNames_AreNeverBound(string shape)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", shape == "attribute"
            ? "namespace Support; public class HelpersAttribute : System.Attribute { }"
            : "namespace Support; public class Helpers { }");
        sources.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { System.String value; void Run() { System.Console.WriteLine(value); } }");
        var beta = shape switch
        {
            "alias" => "using H = Support.Helpers; namespace SampleTests; public class BetaTests { System.String text; void Run() { System.Console.WriteLine(text); var value = new H(); } }",
            "attribute" => "namespace SampleTests; [Support.Helpers] public class BetaTests { System.String text; }",
            _ => "namespace SampleTests; public class BetaTests { System.String text; void Run() { System.Console.WriteLine(text); var value = new Support.Helpers(); } }"
        };
        sources.Add("BetaTests.cs", beta);
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = sources.Select(["Support.cs"], probe);
        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.BetaTests" }, actual.Classes);
        Assert.NotEmpty(probe.BoundNames);
        Assert.All(probe.BoundNames, bound =>
        {
            Assert.Equal("SampleTests.BetaTests", bound.Owner);
            Assert.Equal(shape == "alias" ? "H" : "Helpers", bound.Identifier);
            Assert.Equal(shape == "attribute", bound.AttributePosition);
        });
        Assert.DoesNotContain(probe.BoundNames, bound => bound.Owner == "SampleTests.AlphaTests");
        var nameCount = CSharpSyntaxTree.ParseText(beta).GetRoot().DescendantNodes().OfType<NameSyntax>().Count();
        Assert.True(probe.BoundNames.Count < nameCount, "Non-candidate names must bypass GetSymbolInfo.");
    }

    [Fact]
    public void TwoChangedFiles_WalkEachTreeOnce()
    {
        using var sources = new SourceFixture();
        sources.Add("First.cs", "namespace Support; public class First { }");
        sources.Add("Second.cs", "namespace Support; public class Second { }");
        sources.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Support.First value; }");
        sources.Add("BetaTests.cs", "namespace SampleTests; public class BetaTests { Support.Second value; }");
        sources.Add("BothTests.cs", "namespace SampleTests; public class BothTests { Support.First first; Support.Second second; }");
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = sources.Select(["First.cs", "Second.cs"], probe);
        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests", "SampleTests.BothTests" }, actual.Classes);
        Assert.Empty(actual.OtherFiles);
        Assert.Equal(sources.Projects.SelectMany(project => project.Sources).Order(StringComparer.Ordinal),
            probe.TreePasses.Keys.Order(StringComparer.Ordinal));
        Assert.All(probe.TreePasses.Values, passes => Assert.Equal(1, passes));
        Assert.Contains(probe.BoundNames, bound => bound.Owner == "SampleTests.BothTests" && bound.Identifier == "First");
        Assert.Contains(probe.BoundNames, bound => bound.Owner == "SampleTests.BothTests" && bound.Identifier == "Second");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedFile_PreservesEarlierOtherFilesAndAbandonOrder(bool unusedFirst)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public class Helpers { }");
        sources.Add("Unused.cs", "namespace Support; public class Unused { }");
        sources.Add("elsewhere/OtherTests.cs", "namespace SampleTests; public class OtherTests { Support.Helpers value; }", infrastructure: false);
        File.WriteAllText(Path.Combine(sources.Root, "Outside.cs"), "class Outside { }");
        string[] changed = unusedFirst ? ["Unused.cs", "Support.cs", "Outside.cs"] : ["Support.cs", "Outside.cs", "Unused.cs"];
        var actual = sources.Select(changed);
        var legacy = LegacySelection(sources.Root, changed, sources.Projects, sources.Runnable);
        Assert.Empty(actual.Classes);
        Assert.Equal(unusedFirst ? "Changed test support has no resolvable test class: Unused.cs"
            : "Changed test support could not be resolved: Outside.cs", actual.AbandonReason);
        Assert.Equal(unusedFirst ? [] : new[] { "elsewhere/OtherTests.cs" }, actual.OtherFiles);
        Assert.Equal(legacy.AbandonReason, actual.AbandonReason);
        Assert.Equal(legacy.OtherFiles, actual.OtherFiles);
    }

    [Fact]
    public void DeletedFiles_DoNotEnterReferencePass()
    {
        using var sources = new SourceFixture();
        sources.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { }");
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = sources.Select(["Deleted.cs"], probe);
        Assert.Null(actual.AbandonReason);
        Assert.Empty(actual.Classes);
        Assert.Empty(actual.OtherFiles);
        Assert.Empty(probe.TreePasses);
        Assert.Empty(probe.BoundNames);
    }

    [Fact]
    public void MalformedSource_AbandonsBeforeRunnableDiscovery()
    {
        using var sources = new SourceFixture();
        sources.Add("Bad.cs", "public class {");
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = ImpactReplayChangedSupportReferenceScan.Select(sources.Root, ["Bad.cs"], sources.Projects,
            () => throw new InvalidOperationException("Runnable discovery must follow parsing."), probe);
        Assert.Equal("Changed test support source could not be parsed: Bad.cs", actual.AbandonReason);
        Assert.Empty(actual.Classes);
        Assert.Empty(actual.OtherFiles);
        Assert.Empty(probe.TreePasses);
    }

    // Frozen pre-change per-file loop, with runnable membership injected instead of scanning the repository.
    private static (IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles) LegacySelection(
        string checkout, IReadOnlyList<string> changedFiles,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project> projects, ISet<string> runnable)
    {
        var paths = projects.SelectMany(p => p.Sources).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trees = paths.ToDictionary(p => p, p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p), StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("ReplayTestSources", trees.Values,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var infrastructurePaths = projects.Where(p => p.Infrastructure).SelectMany(p => p.Sources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new SortedSet<string>(StringComparer.Ordinal);
        var other = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in changedFiles)
        {
            var path = Path.GetFullPath(relativePath, checkout);
            if (!File.Exists(path)) continue;
            if (!trees.TryGetValue(path, out var changedTree)) return ([], $"Changed test support could not be resolved: {relativePath}", other.ToArray());
            var changedModel = compilation.GetSemanticModel(changedTree);
            var declared = changedTree.GetRoot().DescendantNodes()
                .Where(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                .Select(n => changedModel.GetDeclaredSymbol(n)).OfType<INamedTypeSymbol>()
                .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in trees.Values)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var node in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol owner) continue;
                    var name = FullName(owner);
                    if (!runnable.Contains(name)) continue;
                    var references = declared.Contains(owner) || node.DescendantNodes().OfType<NameSyntax>().Any(reference =>
                    {
                        var symbol = model.GetSymbolInfo(reference).Symbol;
                        if (symbol is IAliasSymbol alias) symbol = alias.Target;
                        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                            symbol = constructor.ContainingType;
                        return symbol is INamedTypeSymbol type && declared.Contains(type.OriginalDefinition);
                    });
                    if (!references) continue;
                    found.Add(name);
                    if (infrastructurePaths.Contains(tree.FilePath)) selected.Add(name);
                    else other.Add(Path.GetRelativePath(checkout, tree.FilePath).Replace('\\', '/'));
                }
            }
            if (found.Count == 0) return ([], $"Changed test support has no resolvable test class: {relativePath}", other.ToArray());
        }
        return (selected.ToArray(), null, other.ToArray());
    }

    private static string FullName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? FullName(parent) + "+" + type.Name
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.Name;

    private sealed class SourceFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "impact-support-scan-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _infrastructure = [];
        private readonly List<string> _other = [];
        internal ISet<string> Runnable { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "SampleTests.AlphaTests", "SampleTests.BetaTests", "SampleTests.BothTests", "SampleTests.OtherTests"
        };
        internal IReadOnlyList<ImpactReplayChangedSourceRouting.Project> Projects =>
            [new(Path.Combine(Root, "Infrastructure.Tests.csproj"), true, _infrastructure),
             new(Path.Combine(Root, "Other.Tests.csproj"), false, _other)];

        internal void Add(string relativePath, string text, bool infrastructure = true)
        {
            var path = Path.GetFullPath(relativePath, Root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            (infrastructure ? _infrastructure : _other).Add(path);
        }

        internal (IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles) Select(
            IReadOnlyList<string> changed, ImpactReplayChangedSupportScanProbe? probe = null) =>
            ImpactReplayChangedSupportReferenceScan.Select(Root, changed, Projects, () => Runnable, probe);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
