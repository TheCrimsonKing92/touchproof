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
            ["scripts/Example.ps1", "scripts/Other.ps1"], sources, fixture.Classes, null, []);

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
    [InlineData("src/Example.cs", false)]
    [InlineData("docs/readme.md", false)]
    [InlineData("Directory.Build.props", false)]
    [InlineData("tests/Example/Example.csproj", false)]
    [InlineData("config/settings.json", true)]
    [InlineData("config/settings.JsOn", true)]
    public void ScriptsFixturesAndExternalJsonRequireMapping(string changed, bool abandons)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", "public class OrdinaryTests { [Fact] public void Works() {} }");
        var selection = ChangedFileTestClassPathMapper.Read([changed], fixture.Sources, fixture.Classes, []);
        Assert.Equal(abandons ? ReverseDependencySelectionOutcome.Abandoned : ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Empty(selection.TestClassNames);
        if (abandons)
        {
            Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
            Assert.Contains(changed, selection.Reason);
        }
    }

    [Theory]
    [InlineData("config/acceptance-manifest.json", null)]
    [InlineData("./CONFIG\\Acceptance-Manifest.JSON", null)]
    [InlineData("config/acceptance-manifest.json", "scripts/mapped.ps1")]
    [InlineData("config/acceptance-manifest.json", "scripts/unmapped.ps1")]
    public void ManifestChange_AbandonsBeforeLiteralMapping(string manifest, string? companion)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class ManifestTests { [Fact] public void Works() { var path = "config/acceptance-manifest.json"; } }
            public class ScriptTests { [Fact] public void Works() { var path = "scripts/mapped.ps1"; } }
            """);
        string[] changed = companion is null ? [manifest] : [companion, manifest];

        var selection = ChangedFileTestClassPathMapper.Read(changed, fixture.Sources, fixture.Classes, []);

        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
        Assert.Empty(selection.TestClassNames);
        Assert.Contains("config/acceptance-manifest.json", selection.Reason);
    }

    [Fact]
    public void ExternalJson_MapsExactlyClassesNamingPathOrFileName()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class PathTests { [Fact] public void Works() { var path = "./CONFIG/settings.JsOn"; } }
            public class NameTests { [Fact] public void Works() { var name = "settings.json"; } }
            public class OtherTests { [Fact] public void Works() { var text = "prefix settings.json"; } }
            """);

        var selection = ChangedFileTestClassPathMapper.Read(["config/settings.json"], fixture.Sources, fixture.Classes, []);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(new[] { "NameTests", "PathTests" }, selection.TestClassNames);
    }

    [Theory]
    [InlineData("tests/Example.Tests/settings.json", false)]
    [InlineData("tests/Example.Tests/Fixtures/settings.json", true)]
    [InlineData("tests/canary-fixture/config/acceptance-manifest.json", true)]
    public void JsonDirectoryExceptions_PreserveLiteralMapping(string changed, bool maps)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class DataTests { [Fact] public void Works() { var name = "settings.json"; var manifest = "acceptance-manifest.json"; } }
            """);

        var selection = ChangedFileTestClassPathMapper.Read([changed], fixture.Sources, fixture.Classes,
            ["./TESTS\\Example.Tests\\"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(maps ? new[] { "DataTests" } : Array.Empty<string>(), selection.TestClassNames);
    }

    [Fact]
    public void ManifestOnlyReplay_AbandonsNarrowPlanWithShardClassKnown()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class IsolatedProcessSpawningShardCollectionTests { [Fact] public void Works() {} }
            public class OrdinaryTests { [Fact] public void Works() {} }
            """);
        var classes = fixture.Classes;
        Assert.Contains(classes, item => item.FullName == "IsolatedProcessSpawningShardCollectionTests");

        var selection = ImpactReplaySelectionAugmentation.Apply(
            ReverseDependencyTestSelection.Resolved(["OrdinaryTests"]),
            ["config/acceptance-manifest.json"], fixture.Sources, classes, null, []);

        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
        Assert.Empty(selection.TestClassNames);
        Assert.Contains("config/acceptance-manifest.json", selection.Reason);
    }

    [Fact]
    public void PathMappingPreservesExistingDegradationWhenEveryFileMaps()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        fixture.Add("Tests.cs", """
            public class ScriptTests { [Fact] public void Works() { var path = "run.sh"; } }
            """);
        var original = ReverseDependencyTestSelection.Abandoned(ReverseDependencyDegradationKind.ChangedSourceFileCount, "existing limit");
        Assert.Same(original, ImpactReplaySelectionAugmentation.Apply(original, ["scripts/run.sh"], fixture.Sources, fixture.Classes, null, []));
    }

    [Theory]
    [InlineData("./TESTS\\Example.Tests\\TestData\\Fixtures\\new.json", true)]
    [InlineData("tests/Example.Tests/tools/new.ps1", true)]
    [InlineData("tests/Checked.Tests/Fixtures/new.json", true)]
    [InlineData("./TESTS\\Example.Tests\\TestData\\new.JSON", true)]
    [InlineData("tests/Checked.Tests/new.json", true)]
    [InlineData("scripts/new.ps1", false)]
    [InlineData("tests/Example.Tests.Sibling/Fixtures/new.json", false)]
    [InlineData("tests/Undeclared.Tests/Fixtures/new.json", false)]
    [InlineData("tests/Example.Tests.Sibling/new.json", false)]
    [InlineData("tests/Undeclared.Tests/new.json", false)]
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
            [changed], fixture.Sources, fixture.Classes, null, out _, additionalClasses: null, testProjectDirectories: directories);
        Assert.Equal(selection.Outcome, augmented.Outcome);
        if (resolved) Assert.Equal(new[] { "ExistingTests" }, augmented.TestClassNames);
        else Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, augmented.DegradationKind);
    }
    [Theory]
    [InlineData("tools/Sample/data/new.json")]
    [InlineData("tools/Sample/data/new.ps1")]
    [InlineData("tools/Sample/data/new.cmd")]
    [InlineData("tools/Sample/data/new.sh")]
    public void OwnedDataAndScriptsWithoutDeclaredReferencesResolve(string changed)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var existing = AddSource(fixture, "tests/Existing.Tests/Existing.cs",
            "public class ExistingTests { [Fact] public void Works() {} }");
        var owner = AddProject(fixture, "tools/Sample/src/Sample.csproj");
        var undeclared = AddProject(fixture, "tools/Sample/tests/Sample.Tests.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"missing.csproj\" /></ItemGroup></Project>");
        var declared = AddProject(fixture, "tests/Existing.Tests/Existing.Tests.csproj", "<Project />", existing);
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees, [owner, undeclared, declared], fixture.Root);
        var classes = fixture.Classes;
        string[] directories = ["tests/Existing.Tests"];

        var selection = ChangedFileTestClassPathMapper.Read([changed], sources, classes, directories);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Empty(selection.TestClassNames);
        Assert.Null(selection.Reason);
        var augmented = ImpactReplaySelectionAugmentation.Apply(ReverseDependencyTestSelection.Resolved(["ExistingTests"]),
            [changed], sources, classes, null, directories);
        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, augmented.Outcome);
        Assert.Equal(new[] { "ExistingTests" }, augmented.TestClassNames);
    }

    [Theory]
    [InlineData("src/Sample.csproj")]
    [InlineData("tests/Sample.Tests.csproj")]
    public void OwningProjectsSelectExactReferencingClassesAndOtherFilesLiteralMatches(string reference)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var first = AddSource(fixture, "tests/Direct.Tests/First.cs", """
            public partial class DirectTests { [Fact] public void Works() {} }
            public class Outer { public class NestedTests { [Fact] public void Works() {} } }
            """);
        var linked = AddSource(fixture, "shared/Linked.cs", """
            public partial class DirectTests { private int Value; }
            public class LinkedTests { [Fact] public void Works() {} }
            """);
        AddSource(fixture, "tests/Direct.Tests/Excluded.cs",
            "public class ExcludedTests { [Fact] public void Works() {} }");
        var other = AddSource(fixture, "tests/Other.Tests/Other.cs", """
            public class LiteralTests { [Fact] public void Works() { var path = "scripts/literal.ps1"; } }
            public class UnrelatedTests { [Fact] public void Works() {} }
            """);
        var owner = AddProject(fixture, "tools/Sample/src/Sample.csproj");
        var secondOwner = AddProject(fixture, "tools/Sample/tests/Sample.Tests.csproj");
        var direct = AddProject(fixture, "tests/Direct.Tests/Direct.Tests.csproj",
            $"<Project><ItemGroup><ProjectReference Include=\"../../tools/Sample/{reference}\" /></ItemGroup></Project>", first, linked);
        var unrelated = AddProject(fixture, "tests/Other.Tests/Other.Tests.csproj", "<Project />", other);
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees, [owner, secondOwner, direct, unrelated], fixture.Root);

        var selection = ChangedFileTestClassPathMapper.Read(["tools/Sample/data/new.json", "scripts/literal.ps1"],
            sources, fixture.Classes, ["tests/Direct.Tests", "tests/Other.Tests"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(new[] { "DirectTests", "LinkedTests", "LiteralTests", "Outer+NestedTests" }, selection.TestClassNames);
    }

    [Fact]
    public void LiteralMatchedFileKeepsOnlyLiteralClassesDespiteOwningProjectReferences()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var source = AddSource(fixture, "tests/Direct.Tests/Tests.cs", """
            public class LiteralTests { [Fact] public void Works() { var name = "mapped.json"; } }
            public class OtherTests { [Fact] public void Works() {} }
            """);
        var owner = AddProject(fixture, "src/Sample/Sample.csproj");
        var direct = AddProject(fixture, "tests/Direct.Tests/Direct.Tests.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"..\\..\\src\\Sample\\Sample.csproj\" /></ItemGroup></Project>", source);
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees, [owner, direct], fixture.Root);

        var selection = ChangedFileTestClassPathMapper.Read(["src/Sample/mapped.json"], sources, fixture.Classes, ["tests/Direct.Tests"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Equal(new[] { "LiteralTests" }, selection.TestClassNames);
    }

    [Theory]
    [InlineData("scripts/no-owner.ps1")]
    [InlineData("tools/Empty/data/new.json")]
    [InlineData("root.json")]
    [InlineData("config/acceptance-manifest.json")]
    public void UnownedRootAndManifestFilesStillAbandonWithRootAvailable(string changed)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var source = AddSource(fixture, "tests/Existing.Tests/Tests.cs", """
            public class ExistingTests { [Fact] public void Works() {} }
            """);
        var declared = AddProject(fixture, "tests/Existing.Tests/Existing.Tests.csproj", "<Project />", source);
        var sibling = AddProject(fixture, "tools/Sibling/src/Sibling.csproj");
        var rootChild = AddProject(fixture, "src/RootChild.csproj");
        var rootProject = AddProject(fixture, "Root.csproj");
        var manifestOwner = AddProject(fixture, "config/Config.csproj");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "tools/Empty/src"));
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees,
            [declared, sibling, rootChild, rootProject, manifestOwner], fixture.Root);

        var selection = ChangedFileTestClassPathMapper.Read([changed], sources, fixture.Classes, ["tests/Existing.Tests"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
        Assert.Empty(selection.TestClassNames);
        Assert.Contains(changed, selection.Reason);
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("malformed-project")]
    [InlineData("empty-include")]
    [InlineData("property")]
    [InlineData("item")]
    [InlineData("wildcard")]
    [InlineData("missing-target")]
    [InlineData("incomplete-inventory")]
    [InlineData("no-known-classes")]
    public void UnsupportedProjectInspectionAbandonsInsteadOfProvingNoReferences(string unsupported)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var source = AddSource(fixture, "tests/Direct.Tests/Tests.cs",
            "public class DirectTests { [Fact] public void Works() {} }");
        var owner = AddProject(fixture, "src/Sample/Sample.csproj");
        var include = unsupported switch
        {
            "property" => "$(Owner)/Sample.csproj",
            "item" => "@(Projects)",
            "wildcard" => "../../src/*/Sample.csproj",
            "missing-target" => "../../missing.csproj",
            "empty-include" => "",
            _ => "../../src/Sample/Sample.csproj"
        };
        var direct = AddProject(fixture, "tests/Direct.Tests/Direct.Tests.csproj",
            $"<Project><ItemGroup><ProjectReference Include=\"{include}\" /></ItemGroup></Project>", source);
        if (unsupported == "missing-project") File.Delete(direct.Path);
        if (unsupported == "malformed-project") File.WriteAllText(direct.Path, "<Project>");
        if (unsupported == "no-known-classes") direct = direct with { Sources = [] };
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees,
            unsupported == "incomplete-inventory" ? [owner] : [owner, direct], fixture.Root);

        var selection = ChangedFileTestClassPathMapper.Read(["src/Sample/data.json"], sources, fixture.Classes, ["tests/Direct.Tests"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Abandoned, selection.Outcome);
        Assert.Equal(ReverseDependencyDegradationKind.ChangedFileUnmapped, selection.DegradationKind);
        Assert.Empty(selection.TestClassNames);
        Assert.Contains("Cannot determine project test coverage", selection.Reason);
        Assert.Contains("src/Sample/data.json", selection.Reason);
    }

    [Fact]
    public void NearestProjectWinsAndOnlyDirectReferencesSelectClasses()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var source = AddSource(fixture, "tests/Direct.Tests/Tests.cs",
            "public class DirectTests { [Fact] public void Works() {} }");
        var outer = AddProject(fixture, "tools/Outer.csproj");
        var nearest = AddProject(fixture, "tools/Nested/Nested.csproj");
        var direct = AddProject(fixture, "tests/Direct.Tests/Direct.Tests.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"../../tools/Outer.csproj\" /></ItemGroup></Project>", source);
        File.WriteAllText(outer.Path,
            "<Project><ItemGroup><ProjectReference Include=\"Nested/Nested.csproj\" /></ItemGroup></Project>");
        var sources = new ImpactReplayTestSourceIndex(fixture.Sources.Trees, [outer, nearest, direct], fixture.Root);

        var selection = ChangedFileTestClassPathMapper.Read(["tools/Nested/deleted.json"], sources, fixture.Classes, ["tests/Direct.Tests"]);

        Assert.Equal(ReverseDependencySelectionOutcome.Resolved, selection.Outcome);
        Assert.Empty(selection.TestClassNames);
        var outerSelection = ChangedFileTestClassPathMapper.Read(["tools/deleted.json"], sources, fixture.Classes, ["tests/Direct.Tests"]);
        Assert.Equal(new[] { "DirectTests" }, outerSelection.TestClassNames);
    }

    private static string AddSource(ImpactReplaySelectionFixture fixture, string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(fixture.Root, path))!);
        fixture.Add(path, text);
        return Path.Combine(fixture.Root, path);
    }

    private static ImpactReplayChangedSourceRouting.Project AddProject(ImpactReplaySelectionFixture fixture,
        string relativePath, string xml = "<Project />", params string[] sources)
    {
        var path = Path.Combine(fixture.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return new(path, false, sources);
    }
}
