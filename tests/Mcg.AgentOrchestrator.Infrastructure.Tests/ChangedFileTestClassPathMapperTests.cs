using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns a handful of temporary source files; no subprocesses.
public sealed class ChangedFileTestClassPathMapperTests
{
    [Fact]
    public void MapsDecodedLiteralsToEveryOwningTestClassIncludingPartialAndNestedClasses()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("First.cs", """"
            namespace Sample;
            public partial class PathTests { [Fact] public void Works() { var path = "scripts/Example.ps1"; } }
            public class NameTests { [Fact] public void Works() { var name = "\u0045xample.ps1"; } }
            public class RawTests { [Fact] public void Works() { var path = """./SCRIPTS/EXAMPLE.ps1"""; } }
            public class Outer { public class NestedTests { [Fact] public void Works() { var path = @"scripts\Example.ps1"; } } }
            public class CommentTests { [Fact] public void Works() { /* "Example.ps1" */ var text = "prefix Example.ps1"; } }
            public class DynamicTests { [Fact] public void Works() { var text = $"scripts/{1}/Example.ps1"; } }
            """");
        fixture.Add("Partial.cs", """
            namespace Sample;
            public partial class PathTests { private string Path = "Example.ps1"; }
            """);

        var selection = ChangedFileTestClassPathMapper.Read(["./scripts\\Example.ps1"], fixture.Sources, fixture.Classes, []);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(new[] { "Sample.NameTests", "Sample.Outer+NestedTests", "Sample.PathTests", "Sample.RawTests" }, selection.TestClassNames);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "scripts", "Example.ps1"))); // Deleted paths map too.
    }

    [Fact]
    public void UnreferencedScriptAbandonsWholeSelectionEvenWhenAnotherFileMaps()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            namespace Sample;
            public class ScriptTests { [Fact] public void Works() { var path = "scripts/Example.ps1"; } }
            internal class Helper { private string Path = "scripts/Other.ps1"; }
            """);

        var sources = fixture.Sources;
        var selection = ImpactReplaySelectionAugmentation.Apply(ReverseDependencyTestSelection.Resolved(["Sample.ScriptTests"]),
            ["scripts/Example.ps1", "scripts/Other.ps1"], sources, fixture.Classes, null);

        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
        Assert.Empty(selection.TestClassNames);
        Assert.Contains("scripts/Other.ps1", selection.Reason);
    }

    [Theory]
    [InlineData("scripts/run.CMD", true)]
    [InlineData("scripts/run.sh", true)]
    [InlineData("tests/Example/Fixtures/data.json", true)]
    [InlineData("tests/Example/Fixtures/code.cs", false)]
    [InlineData("docs/readme.md", false)]
    [InlineData("config/settings.json", false)]
    public void OnlyExplicitScriptAndFixtureFamilyRequiresMapping(string changed, bool abandons)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", "public class OrdinaryTests { [Fact] public void Works() {} }");
        var selection = ChangedFileTestClassPathMapper.Read([changed], fixture.Sources, fixture.Classes, []);
        Assert.Equal(abandons ? ReverseDependencySelectionOutcome.Abandoned : ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        if (abandons) Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
    }

    [Fact]
    public void PathMappingPreservesExistingDegradationWhenEveryFileMaps()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class ScriptTests { [Fact] public void Works() { var path = "run.sh"; } }
            """);
        var original = ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedSourceFileCount, "existing limit");
        Assert.Same(original, ImpactReplaySelectionAugmentation.Apply(original, ["scripts/run.sh"], fixture.Sources, fixture.Classes, null));
    }

    [Theory]
    [InlineData("./TESTS\\Example.Tests\\TestData\\Fixtures\\new.json", true)]
    [InlineData("tests/Example.Tests/tools/new.ps1", true)]
    [InlineData("tests/Checked.Tests/Fixtures/new.json", true)]
    [InlineData("scripts/new.ps1", false)]
    [InlineData("tests/Example.Tests.Sibling/Fixtures/new.json", false)]
    [InlineData("tests/Undeclared.Tests/Fixtures/new.json", false)]
    public void UnnamedFile_ExemptionUsesOnlyManifestDeclaredProjectDirectories(string changed, bool resolved)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", "public class ExistingTests { [Fact] public void Works() {} }");
        // An on-disk project is insufficient authority without a manifest declaration.
        Directory.CreateDirectory(Path.Combine(fixture.Root, "tests", "Undeclared.Tests"));
        File.WriteAllText(Path.Combine(fixture.Root, "tests", "Undeclared.Tests", "Undeclared.Tests.csproj"), "<Project />");
        var settings = AcceptanceGateEngineSettings.Parse("""
            { "engine": { "mtpInvocations": [ { "project": "tests/Example.Tests/Example.Tests.csproj",
                "executablePathTemplate": "bin/{projectName}/{projectName}{executableExtension}", "arguments": ["{executable}"] } ] },
              "checks": [ { "name": "checked", "type": "dotnet-test", "project": "tests/Checked.Tests/Checked.Tests.csproj" } ] }
            """);
        var directories = ChangedFileTestClassPathMapper.TestProjectDirectories(settings);
        Assert.Equal(new[] { "tests/Example.Tests", "tests/Checked.Tests" }, directories);
        var selection = ChangedFileTestClassPathMapper.Read([changed], fixture.Sources, fixture.Classes, directories);
        Assert.Equal(resolved ? ReverseDependencySelectionOutcome.Resolved : ReverseDependencySelectionOutcome.Abandoned,
            selection.Outcome);
        Assert.Empty(selection.TestClassNames);
        Assert.Equal(resolved ? (ReverseDependencyDegradationKind?)null : ReverseDependencyDegradationKind.ChangedFileUnmapped,
            selection.DegradationKind);

        var augmented = ImpactReplaySelectionAugmentation.Apply(ReverseDependencyTestSelection.Resolved(["ExistingTests"]),
            [changed], fixture.Sources, fixture.Classes, null, out _, testProjectDirectories: directories);
        Assert.Equal(selection.Outcome, augmented.Outcome);
        if (resolved) Assert.Equal(new[] { "ExistingTests" }, augmented.TestClassNames);
        else Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, augmented.DegradationKind);
    }
}
