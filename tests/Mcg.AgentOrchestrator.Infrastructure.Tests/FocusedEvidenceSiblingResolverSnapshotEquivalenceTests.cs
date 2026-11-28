using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: each test fully constructs its own root before obtaining an immutable snapshot.
public sealed class FocusedEvidenceSiblingResolverSnapshotEquivalenceTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Xunit.Fact]
    public void AbstractBaseFamilyReturnsTheSameOrderedSiblingsFromSnapshot()
    {
        using var fixture = CreateFixture();

        var siblings = AssertEquivalent(fixture.Root, "Fixture.RequestedTests");

        Assert.NotEmpty(siblings);
        Assert.Equal(new[] { "AlphaDerivedTests", "ZetaDerivedTests" }, siblings);
        Assert.Contains("AlphaDerivedTests", siblings);
        Assert.DoesNotContain("FamilyTestBase", siblings);
        Assert.DoesNotContain("PublicHelper", siblings);
        Assert.DoesNotContain("NestedProjectTests", siblings);
        var source = File.ReadAllText(Path.Combine(fixture.Root, Path.GetDirectoryName(Project)!, "Base.cs"));
        var directory = Path.Combine(fixture.Root, ".orchestrator", FocusedEvidenceDeclarationFactStore.DirectoryName);
        Assert.True(File.Exists(FocusedEvidenceDeclarationFactStore.EntryPath(
            directory, AcceptanceTestClassSourceExtractStore.HashText(source))));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Fixture.BrokenTests")]
    [Xunit.InlineData("Fixture.UnknownTests")]
    [Xunit.InlineData("Fixture.NestedProjectTests")]
    public void MalformedUnknownOrNestedProjectClassReturnsTheSameEmptyList(string requestedClass)
    {
        using var fixture = CreateFixture();

        Assert.Empty(AssertEquivalent(fixture.Root, requestedClass));
    }

    [Xunit.Fact]
    public void PublicHelperIsExcludedFromTheOrderedSiblings()
    {
        using var fixture = CreateFixture();

        Assert.Equal(new[] { "AlphaDerivedTests", "RequestedTests", "ZetaDerivedTests" },
            AssertEquivalent(fixture.Root, "Fixture.PublicHelper"));
    }

    [Xunit.Fact]
    public void WarningOnlyCandidateKeepsTheOrderedSiblings()
    {
        using var fixture = CreateFixture();
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Warning.cs", """
            #warning Fixture warning
            // RequestedTests
            """);

        Assert.Equal(new[] { "AlphaDerivedTests", "ZetaDerivedTests" },
            AssertEquivalent(fixture.Root, "Fixture.RequestedTests"));
    }

    [Xunit.Fact]
    public void AbstractRecordBaseDoesNotExpandSiblingFiles()
    {
        using var fixture = CreateFixture();
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RecordBase.cs", """
            namespace Fixture;
            public abstract record RecordBase;
            public sealed record RequestedRecordTests : RecordBase { [Xunit.Fact] public void Runs() { } }
            public sealed record SameFileRecordTests : RecordBase { [Xunit.Fact] public void Runs() { } }
            """);
        fixture.Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/RecordDerived.cs", """
            namespace Fixture;
            public sealed record OtherFileRecordTests : RecordBase { [Xunit.Fact] public void Runs() { } }
            """);

        Assert.Equal(new[] { "SameFileRecordTests" }, AssertEquivalent(fixture.Root, "Fixture.RequestedRecordTests"));
    }

    private static IReadOnlyList<string> AssertEquivalent(string root, string requestedClass)
    {
        var expected = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root, Project, requestedClass, File.ReadAllText,
            (path, source) => CSharpSyntaxTree.ParseText(source, path: path));
        var fromFacts = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(root, Project, requestedClass);
        var fromSnapshot = FocusedEvidenceSiblingClassResolver.ResolveSiblingTestClassNames(
            root, Project, requestedClass,
            readText: path => RepositorySourceSnapshotCache.Get(root).GetText(path),
            parse: (path, _) => RepositorySourceSnapshotCache.Get(root).GetSyntaxTree(path));

        Assert.Equal(expected, fromFacts);
        Assert.Equal(expected, fromSnapshot);
        return fromFacts;
    }

    private static SourceSnapshotFixture CreateFixture()
    {
        var fixture = new SourceSnapshotFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".orchestrator"));
        fixture.Write(Project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        const string directory = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/";
        fixture.Write(directory + "Base.cs", """
            namespace Fixture;
            public abstract class FamilyTestBase { }
            public sealed class RequestedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            public sealed class PublicHelper : FamilyTestBase { public void Runs() { } }
            """);
        fixture.Write(directory + "Derived.cs", """
            namespace Fixture;
            public sealed class ZetaDerivedTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            public sealed class AlphaDerivedTests : FamilyTestBase { [Xunit.Theory] public void Runs() { } }
            """);
        fixture.Write(directory + "Nested/Nested.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        fixture.Write(directory + "Nested/NestedProjectTests.cs", """
            namespace Fixture;
            public sealed class NestedProjectTests : FamilyTestBase { [Xunit.Fact] public void Runs() { } }
            """);
        fixture.Write(directory + "Broken.cs", """
            namespace Fixture;
            public sealed class BrokenTests { [Xunit.Fact] public void Runs( { } }
            """);
        fixture.Write(directory + "BrokenFamilyMember.cs", """
            namespace Fixture;
            public sealed class BrokenFamilyMember : FamilyTestBase { [Xunit.Fact] public void Runs( { } }
            """);
        return fixture;
    }
}
