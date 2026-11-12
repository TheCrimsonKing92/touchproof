using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all source sets are in memory, with no cache or process-global state.
public sealed class AcceptanceTestClassSourceScannerDerivedFactTests
{
    [Theory]
    [InlineData("class MarkerAttribute : Xunit.FactAttribute {}", "Marker")]
    [InlineData("class MarkerAttribute : FactAttribute {}", "MarkerAttribute")]
    [InlineData("class MarkerAttribute : global::Xunit.TheoryAttribute {}", "Marker")]
    [InlineData("class ParentAttribute : Xunit.FactAttribute {} class MarkerAttribute : ParentAttribute {}", "Marker")]
    [InlineData("using Root = Xunit.TheoryAttribute; class MarkerAttribute : Root {}", "Marker")]
    [InlineData("using X = Xunit; class MarkerAttribute : X.FactAttribute {}", "Marker")]
    [InlineData("namespace Attributes { class MarkerAttribute : Xunit.FactAttribute {} }", "global::Attributes.Marker")]
    public void DerivedMarker_AcrossFilesAndOrders_ScansExactClass(string declaration, string marker)
    {
        var consumer = $"namespace Cases; class OnlyTests {{ [{marker}] void Runs() {{}} }}";
        foreach (var sources in new[] { new[] { declaration, consumer }, new[] { consumer, declaration } })
            Assert.Equal(["Cases.OnlyTests"], ScanNames(sources));
    }

    [Theory]
    [InlineData("using Attributes;", "Marker")]
    [InlineData("using M = Attributes.MarkerAttribute;", "M")]
    [InlineData("using A = global::Attributes;", "A.Marker")]
    [InlineData("", "Attributes.MarkerAttribute")]
    public void ImportedOrAliasedMarker_SourceTypeResolved_ScansClass(string imports, string marker)
    {
        Assert.Equal(["Cases.OnlyTests"], ScanNames(
            "namespace Attributes { class MarkerAttribute : Xunit.TheoryAttribute {} }",
            $"{imports} namespace Cases; class OnlyTests {{ [{marker}] void Runs() {{}} }}"));
    }

    [Theory]
    [InlineData("class PretendFactAttribute : System.Attribute {}")]
    [InlineData("")]
    [InlineData("class ParentAttribute : System.Attribute {} class PretendFactAttribute : ParentAttribute {}")]
    [InlineData("class ParentAttribute : PretendFactAttribute {} class PretendFactAttribute : ParentAttribute {}")]
    [InlineData("class PretendFactAttribute : ExternalAttribute {}")]
    [InlineData("class FactAttribute : System.Attribute {} class PretendFactAttribute : FactAttribute {}")]
    [InlineData("namespace Other { class PretendFactAttribute : Xunit.FactAttribute {} }")]
    public void NonTestAncestry_FactSuffixDoesNotProveRoot_ScansNothing(string declaration)
    {
        Assert.Empty(ScanNames(declaration,
            "class OnlyTests { [PretendFact] void Runs() {} }"));
    }

    [Fact]
    public void SameShortName_DifferentNamespaces_RecognisesOnlyRootedMarker()
    {
        Assert.Equal(["Real.Tests"], ScanNames("""
            namespace Real {
                class CustomFactAttribute : Xunit.FactAttribute {}
                class Tests { [CustomFact] void Runs() {} }
            }
            namespace Fake {
                class CustomFactAttribute : System.Attribute {}
                class Tests { [CustomFact] void Runs() {} }
            }
            """));
    }

    [Fact]
    public void AmbiguousImports_MatchingShortNames_DoNotEstablishAncestry()
    {
        Assert.Empty(ScanNames("""
            using Real;
            using Fake;
            namespace Real { class CustomFactAttribute : Xunit.FactAttribute {} }
            namespace Fake { class CustomFactAttribute : System.Attribute {} }
            class Tests { [CustomFact] void Runs() {} }
            """));
    }

    [Fact]
    public void ScopedUsings_ApplyOnlyInsideTheirNamespace()
    {
        Assert.Equal(["Visible.Tests"], ScanNames("""
            namespace Attributes { class MarkerAttribute : Xunit.FactAttribute {} }
            namespace Visible {
                using Attributes;
                class Tests { [Marker] void Runs() {} }
            }
            namespace Hidden { class Tests { [Marker] void Runs() {} } }
            """));
    }

    [Fact]
    public void GlobalUsing_SeparateFile_ResolvesMarkerInEitherOrder()
    {
        string[] sources = [
            "global using Attributes;",
            "namespace Attributes { class MarkerAttribute : Xunit.FactAttribute {} }",
            "namespace Cases; class Tests { [Marker] void Runs() {} }"
        ];
        Assert.Equal(["Cases.Tests"], ScanNames(sources));
        Assert.Equal(["Cases.Tests"], ScanNames(sources.Reverse().ToArray()));
    }

    [Fact]
    public void PartialAttribute_InterfaceOnlyPart_RetainsRootedBase()
    {
        Assert.Equal(["Tests"], ScanNames(
            "interface IMarker {} partial class MarkerAttribute : IMarker {}",
            "partial class MarkerAttribute : Xunit.FactAttribute {}",
            "class Tests { [Marker] void Runs() {} }"));
    }

    [Fact]
    public void NestedAndPartialTypes_ResolveAcrossParts_PreserveClassMetadata()
    {
        var actual = AcceptanceTestClassSourceScanner.ScanSourceTexts([
            """
            namespace Cases;
            partial class Outer {
                class MarkerAttribute : Xunit.FactAttribute {}
                [Xunit.Collection("Synthetic")] partial class Tests {}
            }
            """,
            """
            namespace Cases;
            partial class Outer {
                partial class Tests { [Marker] void Runs() {} }
            }
            """
        ]);
        Assert.Equal(new AcceptanceTestClassDescriptor("Cases.Outer+Tests", "Synthetic"), Assert.Single(actual));
    }

    [Fact]
    public void LongAncestry_SourceDefinedChain_HasNoDepthLimit()
    {
        var declarations = Enumerable.Range(0, 128).Select(index =>
            $"class Marker{index}Attribute : {(index == 0 ? "Xunit.TheoryAttribute" : $"Marker{index - 1}Attribute")} {{}}");
        Assert.Equal(["Tests"], ScanNames(declarations.Append(
            "class Tests { [Marker127] void Runs() {} }").Reverse().ToArray()));
    }

    [Fact]
    public void DerivedFacts_AbstractBaseAndConcreteChild_PreserveEligibility()
    {
        Assert.Equal(["Cases.Child"], ScanNames("""
            namespace Cases;
            class MarkerAttribute : Xunit.FactAttribute {}
            abstract class Parent { [Marker] void Runs() {} }
            class Child : Parent {}
            """));
    }

    private static string[] ScanNames(params string[] sources) =>
        AcceptanceTestClassSourceScanner.ScanSourceTexts(sources).Select(item => item.FullName).ToArray();
}
