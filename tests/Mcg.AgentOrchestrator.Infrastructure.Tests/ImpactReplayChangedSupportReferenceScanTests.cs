using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Parallel-safe: temporary sources and probes are per case, and the shared CoreLib reference is immutable.
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
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = sources.Select(changed, probe);
        var legacy = LegacySelection(sources.Root, changed, sources.Projects, sources.Runnable, out var legacyReference);
        Assert.Same(ImpactReplayChangedSupportReferenceScan.CoreLib.Reference, probe.CompilationReference);
        Assert.Same(probe.CompilationReference, legacyReference);
        var hasReferrer = shape is not ("collision" or "unreferenced");
        Assert.Null(actual.AbandonReason);
        Assert.Equal(hasReferrer ? new[] { "SampleTests.BetaTests" } : [], actual.Classes);
        Assert.Equal(hasReferrer ? new[] { "elsewhere/OtherTests.cs" } : [], actual.OtherFiles);
        Assert.Equal(legacy.Classes, actual.Classes);
        Assert.Equal(legacy.AbandonReason, actual.AbandonReason);
        Assert.Equal(legacy.OtherFiles, actual.OtherFiles);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("base")]
    [InlineData("struct")]
    [InlineData("record")]
    [InlineData("record-struct")]
    [InlineData("interface")]
    [InlineData("delegate-return")]
    [InlineData("delegate-parameter")]
    [InlineData("abstract-base")]
    [InlineData("two-hop")]
    [InlineData("alias")]
    [InlineData("generic")]
    [InlineData("partial")]
    [InlineData("nested")]
    [InlineData("constraint")]
    [InlineData("attribute")]
    [InlineData("enum")]
    public void SupportChain_SelectsRunnableClassThroughNonRunnableFixture(string shape)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", shape == "generic"
            ? "namespace Support; public class Helpers<T> { }"
            : shape == "attribute"
                ? "namespace Support; public class HelpersAttribute : System.Attribute { }"
                : "namespace Support; public class Helpers { public const int Value = 1; }");
        var fixture = shape switch
        {
            "base" => "public class HelperFixture : Support.Helpers { }",
            "struct" => "public struct HelperFixture { Support.Helpers value; }",
            "record" => "public record HelperFixture { Support.Helpers value; }",
            "record-struct" => "public record struct HelperFixture { Support.Helpers value; }",
            "interface" => "public interface HelperFixture { Support.Helpers Make(); }",
            "delegate-return" => "public delegate Support.Helpers HelperFixture();",
            "delegate-parameter" => "public delegate void HelperFixture(Support.Helpers value);",
            "abstract-base" => "public abstract class HelperFixture { Support.Helpers value; }",
            "two-hop" => "public class HelperFixture { Bridge value; } public class Bridge { Support.Helpers value; }",
            "alias" => "public class HelperFixture { H value; }",
            "generic" => "public class HelperFixture<T> { Support.Helpers<T> value; }",
            "partial" => "public partial class HelperFixture { Support.Helpers value; }",
            "nested" => "public class Outer { public class HelperFixture { Support.Helpers value; } }",
            "constraint" => "public class HelperFixture<T> where T : Support.Helpers { }",
            "attribute" => "[Support.Helpers] public class HelperFixture { }",
            "enum" => "public enum HelperFixture { Value = Support.Helpers.Value }",
            _ => "public class HelperFixture { Support.Helpers value; }"
        };
        sources.Add("Fixture.cs", (shape == "alias" ? "using H = Support.Helpers; " : "") + "namespace Fixtures; " + fixture);
        if (shape == "partial") sources.Add("FixturePart.cs", "namespace Fixtures; public partial class HelperFixture { }");
        var beta = shape switch
        {
            "abstract-base" => "namespace SampleTests; public class BetaTests : Fixtures.HelperFixture { }",
            "alias" => "using F = Fixtures.HelperFixture; namespace SampleTests; public class BetaTests { F value; }",
            "generic" => "namespace SampleTests; public class BetaTests { Fixtures.HelperFixture<int> value; }",
            "nested" => "namespace SampleTests; public class BetaTests { Fixtures.Outer.HelperFixture value; }",
            "constraint" => "namespace SampleTests; public class BetaTests { System.Type Value() => typeof(Fixtures.HelperFixture<>); }",
            _ => "namespace SampleTests; public class BetaTests { Fixtures.HelperFixture value; }"
        };
        sources.Add("BetaTests.cs", beta);
        sources.Add("elsewhere/OtherTests.cs", beta.Replace("BetaTests", "OtherTests", StringComparison.Ordinal), infrastructure: false);

        var actual = sources.Select(["Support.cs"]);

        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.BetaTests" }, actual.Classes);
        Assert.Equal(new[] { "elsewhere/OtherTests.cs" }, actual.OtherFiles);
    }

    [Theory]
    [InlineData("unreferenced")]
    [InlineData("collision")]
    [InlineData("comment-and-string")]
    [InlineData("companion-ordered")]
    [InlineData("companion-reversed")]
    [InlineData("cycle")]
    public void UnreferencedSupport_SelectsNothingExtraWithoutFullRun(string shape)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", shape == "cycle"
            ? "namespace Support; public class Helpers { Partner value; }"
            : "namespace Support; public class Helpers { }");
        if (shape == "cycle") sources.Add("Partner.cs", "namespace Support; public class Partner { Helpers value; }");
        sources.Add("Unused.cs", "namespace Support; public class Unused { }");
        sources.Add("Other.cs", "namespace Other; public class Helpers { }");
        sources.Add("Fixture.cs", "namespace Fixtures; " + (shape switch
        {
            "collision" => "public class HelperFixture { Other.Helpers value; }",
            "comment-and-string" => "public class HelperFixture { /* Support.Helpers */ string value = \"Support.Helpers\"; }",
            _ => "public class HelperFixture { int value; }"
        }));
        sources.Add("BetaTests.cs", "namespace SampleTests; public class BetaTests { Fixtures.HelperFixture value; }");

        string[] changed = shape switch
        {
            "companion-ordered" => ["Support.cs", "Unused.cs"],
            "companion-reversed" => ["Unused.cs", "Support.cs"],
            _ => ["Support.cs"]
        };
        var actual = sources.Select(changed);

        Assert.Empty(actual.Classes);
        Assert.Null(actual.AbandonReason);
        Assert.Empty(actual.OtherFiles);
        Assert.Equal(changed.Order(StringComparer.Ordinal), actual.SupportSelections.Select(summary => summary.Path));
        Assert.All(actual.SupportSelections, summary =>
        {
            Assert.True(summary.Complete);
            Assert.False(summary.Unresolved);
            Assert.False(summary.FullRunTriggered);
            Assert.Empty(summary.Reasons);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportCycle_TerminatesWithAndWithoutRunnableReferrer(bool referenced)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public class A { B value; }");
        sources.Add("Partner.cs", "namespace Support; public class B { A value; }");
        sources.Add("BetaTests.cs", referenced
            ? "namespace SampleTests; public class BetaTests { Support.B value; }"
            : "namespace SampleTests; public class BetaTests { int value; }");

        var actual = sources.Select(["Support.cs"]);

        Assert.Equal(referenced ? new[] { "SampleTests.BetaTests" } : [], actual.Classes);
        Assert.Null(actual.AbandonReason);
        Assert.Empty(actual.OtherFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectMatch_SupplementsWithSupportChainMatches(bool includeUnusedCompanion)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public class Helpers { }");
        sources.Add("Unused.cs", "namespace Support; public class Unused { }");
        sources.Add("Fixture.cs", "namespace Fixtures; public class HelperFixture { Support.Helpers value; }");
        sources.Add("AlphaTests.cs", "namespace SampleTests; public class AlphaTests { Support.Helpers value; }");
        sources.Add("BetaTests.cs", "namespace SampleTests; public class BetaTests { Fixtures.HelperFixture value; }");
        sources.Add("elsewhere/OtherTests.cs", "namespace SampleTests; public class OtherTests { Fixtures.HelperFixture value; }", infrastructure: false);

        var actual = sources.Select(includeUnusedCompanion ? ["Support.cs", "Unused.cs"] : ["Support.cs"]);

        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.BetaTests" }, actual.Classes);
        Assert.Equal(new[] { "elsewhere/OtherTests.cs" }, actual.OtherFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherProjectMatch_SurvivesUnusedCompanionInEitherOrder(bool unusedFirst)
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "namespace Support; public class Helpers { }");
        sources.Add("Unused.cs", "namespace Support; public class Unused { }");
        sources.Add("Fixture.cs", "namespace Fixtures; public class HelperFixture { Support.Helpers value; }");
        sources.Add("elsewhere/OtherTests.cs", "namespace SampleTests; public class OtherTests { Fixtures.HelperFixture value; }", infrastructure: false);

        var actual = sources.Select(unusedFirst ? ["Unused.cs", "Support.cs"] : ["Support.cs", "Unused.cs"]);

        Assert.Empty(actual.Classes);
        Assert.Null(actual.AbandonReason);
        Assert.Equal(new[] { "elsewhere/OtherTests.cs" }, actual.OtherFiles);
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
        string[] changed = unusedFirst ? ["Unused.cs", "Outside.cs", "Support.cs"] : ["Support.cs", "Outside.cs", "Unused.cs"];
        var actual = sources.Select(changed);
        var legacy = LegacySelection(sources.Root, changed, sources.Projects, sources.Runnable, out _);
        Assert.Empty(actual.Classes);
        Assert.Equal("Changed test support could not be resolved: Outside.cs", actual.AbandonReason);
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
        var summary = Assert.Single(actual.SupportSelections);
        Assert.True(summary.Unresolved);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal("Deleted.cs", summary.Path);
    }

    [Fact]
    public void MalformedSource_AbandonsBeforeRunnableDiscovery()
    {
        using var sources = new SourceFixture();
        sources.Add("Bad.cs", "public class {");
        sources.Add("Support.cs", "public class Helpers { }");
        var probe = new ImpactReplayChangedSupportScanProbe();
        var actual = ImpactReplayChangedSupportReferenceScan.Select(sources.Root, ["Support.cs", "Bad.cs"], sources.Projects,
            () => throw new InvalidOperationException("Runnable discovery must follow parsing."), probe);
        Assert.Equal("Changed test support source could not be parsed: Bad.cs", actual.AbandonReason);
        Assert.Empty(actual.Classes);
        Assert.Empty(actual.OtherFiles);
        Assert.Empty(probe.TreePasses);
        Assert.Equal(new[] { "Bad.cs", "Support.cs" }, actual.SupportSelections.Select(summary => summary.Path));
        Assert.All(actual.SupportSelections, summary =>
        {
            Assert.False(summary.Complete);
            Assert.True(summary.Unresolved);
            Assert.True(summary.FullRunTriggered);
            Assert.Equal(new[] { "Infrastructure.Tests.csproj", "Other.Tests.csproj" }, summary.AffectedProjects);
            Assert.Equal(new[] { "Changed test support source could not be parsed: Bad.cs" }, summary.Reasons);
        });
    }

    [Fact]
    public void SupportWithoutTypeDeclarations_StillAbandons()
    {
        using var sources = new SourceFixture();
        sources.Add("Empty.cs", "using System;");

        var actual = sources.Select(["Empty.cs"]);

        Assert.Equal("Changed test support has no resolvable test class: Empty.cs", actual.AbandonReason);
        var summary = Assert.Single(actual.SupportSelections);
        Assert.True(summary.Unresolved);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal(new[] { "no resolvable support declarations" }, summary.Reasons);
    }

    [Fact]
    public void TruncatedClosureWithoutRunnableReferrer_AffectsEveryTestProject()
    {
        using var sources = new SourceFixture();
        sources.Add("Support.cs", "public class Helpers { }");
        sources.Add("Bridge.cs", "public class Bridge { Helpers value; }");

        var actual = sources.Select(["Support.cs"], maximumVisited: 1);

        Assert.Empty(actual.Classes);
        var summary = Assert.Single(actual.SupportSelections);
        Assert.True(summary.Truncated);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal(new[] { "Infrastructure.Tests.csproj", "Other.Tests.csproj" }, summary.AffectedProjects);
        Assert.Equal(new[] { "support closure truncated at 1 declarations" }, summary.Reasons);
    }

    [Theory]
    [InlineData("Fact")]
    [InlineData("Xunit.Fact")]
    [InlineData("Xunit.FactAttribute")]
    [InlineData("Theory")]
    [InlineData("Xunit.TheoryAttribute")]
    public void UninventoriedTestMethods_StillAbandon(string attribute)
    {
        using var sources = new SourceFixture();
        sources.Add("BrokenTests.cs", $"public class BrokenTests {{ [{attribute}] public void Works() {{ }} }}");

        var actual = sources.Select(["BrokenTests.cs"]);

        Assert.Equal("Changed test support has no resolvable test class: BrokenTests.cs", actual.AbandonReason);
        Assert.Empty(actual.Classes);
        var summary = Assert.Single(actual.SupportSelections);
        Assert.True(summary.Unresolved);
        Assert.True(summary.FullRunTriggered);
        Assert.Equal(new[] { "no resolvable test class" }, summary.Reasons);
    }

    // Independent per-file scan with injected runnable membership. Typed files without matches now contribute nothing.
    private static (IReadOnlyList<string> Classes, string? AbandonReason, IReadOnlyList<string> OtherFiles) LegacySelection(
        string checkout, IReadOnlyList<string> changedFiles,
        IReadOnlyList<ImpactReplayChangedSourceRouting.Project> projects, ISet<string> runnable,
        out MetadataReference compilationReference)
    {
        var paths = projects.SelectMany(p => p.Sources).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trees = paths.ToDictionary(p => p, p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p), StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("ReplayTestSources", trees.Values,
            [ImpactReplayChangedSupportReferenceScan.CoreLib.Reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilationReference = compilation.References.Single();
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
            if (declared.Count == 0) return ([], $"Changed test support has no resolvable test class: {relativePath}", other.ToArray());
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
                    if (infrastructurePaths.Contains(tree.FilePath)) selected.Add(name);
                    else other.Add(Path.GetRelativePath(checkout, tree.FilePath).Replace('\\', '/'));
                }
            }
        }
        return (selected.ToArray(), null, other.ToArray());
    }

    private static string FullName(INamedTypeSymbol type) => type.ContainingType is { } parent
        ? FullName(parent) + "+" + type.Name
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.Name;

    internal sealed class SourceFixture : IDisposable
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

        internal ImpactReplayChangedSupportSelection Select(
            IReadOnlyList<string> changed, ImpactReplayChangedSupportScanProbe? probe = null,
            int threshold = ImpactReplayChangedSupportReferenceScan.DefaultClosureThreshold,
            int maximumVisited = ImpactReplayChangedSupportReferrerClosure.DefaultMaximumVisited) =>
            ImpactReplayChangedSupportReferenceScan.Select(Root, changed, Projects, () => Runnable, probe, threshold, maximumVisited);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
