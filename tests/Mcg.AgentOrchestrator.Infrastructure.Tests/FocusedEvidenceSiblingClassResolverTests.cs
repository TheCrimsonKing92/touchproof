using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceSiblingClassResolverTests
{
    private const string ProjectDirectory = "tests/Fixture.Tests/";
    private const string Project = ProjectDirectory + "Fixture.Tests.csproj";
    // A different memo key for the same files and owner-scoped cache.
    private const string ProjectAlias = ProjectDirectory + "./Fixture.Tests.csproj";
    private const string RequestedClass = "RequestedTests";

    [Xunit.Fact]
    public void SharedFileReturnsOnlyConcretePublicTestSiblings()
    {
        var root = CreateProject(
            """
            namespace Fixture;
            public sealed class RequestedTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FirstSiblingTests { [Xunit.Theory] public void Runs() { } }
            public sealed class CustomSiblingTests { [OptInFact] public void Runs() { } }
            public sealed record RecordSiblingTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FourthSiblingTests { [Xunit.Fact] public void Runs() { } }
            public sealed class FifthSiblingTests { [Xunit.Fact] public void Runs() { } }
            public abstract class AbstractTests { [Xunit.Fact] public void Runs() { } }
            public static class StaticTests { [Xunit.Fact] public static void Runs() { } }
            internal sealed class InternalTests { [Xunit.Fact] public void Runs() { } }
            public record struct RecordStructTests { [Xunit.Fact] public void Runs() { } }
            public sealed class Container { public class NestedTests { [Xunit.Fact] public void Runs() { } } }
            internal sealed class InternalHelper { public void Runs() { } }
            public sealed class PublicHelper { public void Runs() { } }
            internal sealed class OptInFactAttribute : System.Attribute { }
            """);

        var siblings = Resolve(root, "Fixture.RequestedTests");

        Assert.Contains("FirstSiblingTests", siblings);
        Assert.Contains("CustomSiblingTests", siblings);
        Assert.Contains("RecordSiblingTests", siblings);
        Assert.Contains("FourthSiblingTests", siblings);
        Assert.Contains("FifthSiblingTests", siblings);
        Assert.DoesNotContain("RequestedTests", siblings);
        Assert.DoesNotContain("AbstractTests", siblings);
        Assert.DoesNotContain("StaticTests", siblings);
        Assert.DoesNotContain("InternalTests", siblings);
        Assert.DoesNotContain("RecordStructTests", siblings);
        Assert.DoesNotContain("NestedTests", siblings);
        Assert.DoesNotContain("InternalHelper", siblings);
        Assert.DoesNotContain("PublicHelper", siblings);
        Assert.DoesNotContain("OptInFactAttribute", siblings);
    }

    [Xunit.Fact]
    public void StoredFactsAvoidParsingWithAFreshStore()
    {
        using var fixture = CreateStoredFamilyFixture();
        var requestedPath = Path.Combine(fixture.Root, ProjectDirectory, "Tests.cs");
        var derivedPath = Path.Combine(fixture.Root, ProjectDirectory, "Derived.cs");
        using (var parses = FocusedEvidenceParsedRootCache.Observe(fixture.Root))
        {
            Assert.Equal(new[] { "DerivedTests" }, ResolveWithColdStore(fixture.Root, Project));
            Assert.Equal(1, parses.CountFor(requestedPath));
            Assert.Equal(1, parses.CountFor(derivedPath));
        }
        Assert.True(File.Exists(EntryFor(fixture.Root, requestedPath)));
        Assert.True(File.Exists(EntryFor(fixture.Root, derivedPath)));

        using var freshParses = FocusedEvidenceParsedRootCache.Observe(fixture.Root);
        Assert.Equal(new[] { "DerivedTests" }, ResolveWithColdStore(fixture.Root, ProjectAlias));
        Assert.Equal(0, freshParses.Total);
    }

    [Xunit.Theory]
    [Xunit.InlineData("truncated")]
    [Xunit.InlineData("hash")]
    [Xunit.InlineData("public-missing")]
    [Xunit.InlineData("static-missing")]
    public void InvalidStoredFactsReparseOnlyTheAffectedCandidate(string defect)
    {
        using var fixture = CreateStoredFamilyFixture();
        var expected = ResolveWithColdStore(fixture.Root, Project);
        Assert.Equal(new[] { "DerivedTests" }, expected);
        var requestedPath = Path.Combine(fixture.Root, ProjectDirectory, "Tests.cs");
        var entryPath = EntryFor(fixture.Root, requestedPath);
        var entry = File.ReadAllText(entryPath);
        var envelope = JsonNode.Parse(entry)!.AsObject();
        var shape = envelope["Facts"]!["Types"]![1]!["Shape"]!.AsObject();
        switch (defect)
        {
            case "truncated":
                break;
            case "hash":
                envelope["ContentHash"] = "stale";
                break;
            case "public-missing":
                Assert.True(shape.Remove("IsPublic"));
                break;
            case "static-missing":
                Assert.True(shape.Remove("IsStatic"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
        File.WriteAllText(entryPath, defect == "truncated" ? entry[..^1] : envelope.ToJsonString());

        using var parses = FocusedEvidenceParsedRootCache.Observe(fixture.Root);
        Assert.Equal(expected, ResolveWithColdStore(fixture.Root, ProjectAlias));
        Assert.Equal(1, parses.CountFor(requestedPath));
        Assert.Equal(1, parses.Total);
    }

    private static SourceSnapshotFixture CreateStoredFamilyFixture()
    {
        var fixture = new SourceSnapshotFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".orchestrator"));
        fixture.Write(Project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write(ProjectDirectory + "Tests.cs", """
            #warning Fixture warning
            public abstract class FamilyBase { }
            public sealed class RequestedTests : FamilyBase { [Xunit.Fact] public void Runs() { } }
            """);
        fixture.Write(ProjectDirectory + "Derived.cs",
            "public sealed class DerivedTests : FamilyBase { [Xunit.Fact] public void Runs() { } }");
        return fixture;
    }

    private static string EntryFor(string root, string path) => FocusedEvidenceDeclarationFactStore.EntryPath(
        Path.Combine(root, ".orchestrator", FocusedEvidenceDeclarationFactStore.DirectoryName),
        AcceptanceTestClassSourceExtractStore.HashText(File.ReadAllText(path)));

    private static IReadOnlyList<string> ResolveWithColdStore(string root, string project)
    {
        using var store = FocusedEvidenceDeclarationFactStore.Use(new FocusedEvidenceDeclarationFactStore(
            new FocusedEvidenceParsedRootCache.Store(FocusedEvidenceParsedRootCache.Capacity)));
        return FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(root, project, RequestedClass);
    }

    [Xunit.Fact]
    public void UnknownOrAmbiguousClassReturnsNoSiblings()
    {
        var root = CreateProject(
            "public sealed class DuplicateTests { [Xunit.Fact] public void Runs() { } }");
        File.WriteAllText(
            Path.Combine(root, "tests", "Fixture.Tests", "Duplicate.cs"),
            "public sealed class DuplicateTests { [Xunit.Fact] public void AlsoRuns() { } }");

        Assert.Empty(Resolve(root, "MissingTests"));
        Assert.Empty(Resolve(root, "DuplicateTests"));
    }

    [Xunit.Fact]
    public void NestedProjectClassesAreNotReturnedAsSiblings()
    {
        var root = CreateProject(
            """
            public sealed class RequestedTests { [Xunit.Fact] public void Runs() { } }
            public sealed class MainSiblingTests { [Xunit.Fact] public void Runs() { } }
            """);
        var nested = Path.Combine(root, "tests", "Fixture.Tests", "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Nested.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(nested, "NestedTests.cs"),
            "public sealed class NestedTests { [Xunit.Fact] public void Runs() { } }");

        var siblings = Resolve(root, "RequestedTests");

        Assert.Contains("MainSiblingTests", siblings);
        Assert.DoesNotContain("NestedTests", siblings);
    }

    [Xunit.Fact]
    public void RealVerifierFileIncludesBuildSlotSiblingAndExcludesHelpers()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();

        var siblings = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root,
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            "GoalAcceptanceVerifierTests",
            readText: path => RepositorySourceSnapshotCache.Get(root).GetText(path),
            parse: (path, _) => RepositorySourceSnapshotCache.Get(root).GetSyntaxTree(path));

        Assert.Contains("GoalAcceptanceVerifierDotnetBuildSlotTestsGateHeartbeat", siblings);
        Assert.Contains("RealProcessShardAlphaSmokeTests", siblings);
        Assert.DoesNotContain("GoalAcceptanceVerifierTestBase", siblings);
        Assert.DoesNotContain("RecordingTimeProvider", siblings);
        Assert.DoesNotContain("AcceptanceManifestTestDefaults", siblings);
        Assert.DoesNotContain("OptInRealAcceptanceVerifierFactAttribute", siblings);
    }

    private static string CreateProject(string source)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var projectDirectory = Path.Combine(root, "tests", "Fixture.Tests");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(projectDirectory, "Tests.cs"), source);
        return root;
    }

    private static IReadOnlyList<string> Resolve(string root, string requestedClass) =>
        FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root,
            Project,
            requestedClass);
}
