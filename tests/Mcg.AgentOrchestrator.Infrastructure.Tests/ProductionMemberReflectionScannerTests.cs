using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using static ProductionMemberReflectionScanner;

// Parallel-safe: compilations and fixtures are in memory, with no shared mutable state.
public sealed class ProductionMemberReflectionScannerTests
{
    private const string FilePath = "tests/Fixture/Cases.cs";
    private const string Production = "Mcg.AgentOrchestrator.ScannerFixture";
    private static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path)).ToArray();

    [Theory]
    [InlineData("typeof(P.Target).GetMethod(\"Hidden\");", "GetMethod", "Hidden")]
    [InlineData("typeof(P.Target)\n.GetProperty(\n\"Value\"\n);", "GetProperty", "Value")]
    [InlineData("var t = typeof(Alias); var alias = t; alias.GetMethod(\"Hidden\");", "GetMethod", "Hidden")]
    [InlineData("const string name = \"Hidden\"; typeof(P.Target).GetMethod(name);", "GetMethod", "Hidden")]
    [InlineData("typeof(P.Target).GetMethod(\"Hid\" + \"den\");", "GetMethod", "Hidden")]
    [InlineData("string name = HelperName(); typeof(P.Target).GetMethod(name);", "GetMethod", "name")]
    [InlineData("typeof(P.Target).GetMethod(name: \"Hidden\", bindingAttr: BindingFlags.NonPublic);", "GetMethod", "Hidden")]
    [InlineData("var t = typeof(P.Target); t?.GetProperty(\"Value\");", "GetProperty", "Value")]
    public void ProductionLookupIsRejected(string body, string api, string member)
    {
        var result = Fixture(body);
        var lookup = Assert.Single(result.Lookups);
        Assert.Equal("production-member", lookup.Classification);
        Assert.Equal("P.Target", lookup.Target);
        Assert.Equal(api, lookup.Api);
        Assert.Equal(member, lookup.Member);
        Assert.Contains(Production, lookup.Resolution, StringComparison.Ordinal);
        Assert.Equal(lookup.ToString(), Assert.Single(result.Diagnostics));
    }

    [Theory]
    [InlineData("typeof(Cases).GetMethod(\"Check\");")]
    [InlineData("GetType().GetMethod(\"Check\");")]
    [InlineData("typeof(string).GetProperty(\"Length\");")]
    [InlineData("typeof(Mcg.AgentOrchestrator.Fixture).GetMethod(\"Check\");")]
    public void FixtureAndThirdPartyTargetsAreExcluded(string body)
    {
        var result = Fixture(body);
        Assert.Equal("excluded-target", Assert.Single(result.Lookups).Classification);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("HelperType().GetMethod(\"Hidden\");")]
    [InlineData("Type.GetType(\"P.Target\")!.GetProperty(\"Value\");")]
    [InlineData("var t = typeof(string); t = typeof(P.Target); t.GetMethod(\"Hidden\");")]
    [InlineData("var t = typeof(P.Target); Change(ref t); t.GetMethod(\"Hidden\");")]
    public void UnknownTargetsRequireEvidenceWithoutProductionClassification(string body)
    {
        var result = Fixture(body);
        var lookup = Assert.Single(result.Lookups);
        Assert.Equal("unresolved-target", lookup.Classification);
        Assert.Contains("exact reasoned exemption", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
        Assert.DoesNotContain("production-member", result.Diagnostics[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedTargetChangingToProductionCannotEscape()
    {
        const string body = "typeof(P.Target).GetMethod(\"Hidden\");";
        Assert.Empty(Fixture(body, production: false).Diagnostics);
        Assert.Contains("production-member", Assert.Single(Fixture(body).Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonAndNameFreeStackInspectionAreOutsidePolicy()
    {
        var result = Fixture("""
            using var json = System.Text.Json.JsonDocument.Parse("{}");
            json.RootElement.GetProperty("name");
            new System.Diagnostics.StackFrame().GetMethod();
            """);
        Assert.Empty(result.Lookups);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("typeof(P.Target).GetMethod(\"Hidden\");")]
    [InlineData("HelperType().GetMethod(\"Hidden\");")]
    public void ExactExemptionAcceptsOneProvenOrUnknownSite(string body)
    {
        var result = Fixture(body, exemptions: [Allowed() with
            { Receiver = body.StartsWith("HelperType", StringComparison.Ordinal) ? "HelperType()" : "typeof(P.Target)" }]);
        Assert.Single(result.Lookups);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("Path", "tests/*/Cases.cs")]
    [InlineData("Path", "tests/../Cases.cs")]
    [InlineData("Path", "")]
    [InlineData("Callable", "*")]
    [InlineData("Api", "GetField")]
    [InlineData("Member", "")]
    [InlineData("Reason", " ")]
    public void MalformedOrReasonlessExemptionsAreRejected(string field, string value)
    {
        var original = Allowed();
        var exemption = field switch
        {
            "Path" => original with { Path = value },
            "Callable" => original with { Callable = value },
            "Api" => original with { Api = value },
            "Member" => original with { Member = value },
            _ => original with { Reason = value }
        };
        Assert.Contains(Fixture("typeof(P.Target).GetMethod(\"Hidden\");", exemptions: [exemption]).Diagnostics,
            diagnostic => diagnostic.StartsWith("Malformed or reasonless", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateExemptionsFail()
    {
        Assert.Contains(Fixture("typeof(P.Target).GetMethod(\"Hidden\");", exemptions: [Allowed(), Allowed()]).Diagnostics,
            diagnostic => diagnostic.StartsWith("Duplicate", StringComparison.Ordinal));
    }

    [Fact]
    public void AmbiguousCallSiteIdentityFails()
    {
        Assert.StartsWith("Ambiguous", Assert.Single(Fixture("""
            typeof(P.Target).GetMethod("Hidden");
            typeof(P.Target).GetMethod("Hidden");
            """, exemptions: [Allowed()]).Diagnostics), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("typeof(string).GetMethod(\"Hidden\");")]
    [InlineData("typeof(P.Target).GetMethod(\"Other\");")]
    public void StaleOrUnrelatedExemptionsFail(string body)
    {
        Assert.Contains(Fixture(body, exemptions: [Allowed()]).Diagnostics,
            diagnostic => diagnostic.StartsWith("Stale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"unexpected\":true}]")]
    [InlineData("broken")]
    public void InvalidExemptionStoreFailsLoudly(string json) =>
        Assert.NotNull(Record.Exception(() => ReadExemptions(json)));

    private static Exemption Allowed() => new(FilePath, "Cases.Check()", "GetMethod", "Hidden", "typeof(P.Target)",
        "Fixture metadata contract: lookup is inspected, never invoked.");

    private static Result Fixture(string body, bool production = true, Exemption[]? exemptions = null)
    {
        var assembly = CSharpCompilation.Create(Production,
            [CSharpSyntaxTree.ParseText("namespace P; public class Target { private void Hidden() {} public int Value => 0; }")],
            Framework, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = assembly.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var tree = CSharpSyntaxTree.ParseText($$"""
            using System;
            using System.Reflection;
            using Alias = P.Target;
            class Cases {
                public void Check() { {{body}} }
                static Type HelperType() => typeof(P.Target);
                static string HelperName() => "Hidden";
                static void Change(ref Type value) { value = typeof(string); }
            }
            namespace Mcg.AgentOrchestrator { class Fixture {} }
            """, path: FilePath);
        var compilation = CSharpCompilation.Create("ScannerFixture.Tests", [tree],
            Framework.Append(MetadataReference.CreateFromImage(image.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return Scan(compilation, [tree], production ? new HashSet<string> { Production } : new HashSet<string>(), exemptions ?? []);
    }
}
