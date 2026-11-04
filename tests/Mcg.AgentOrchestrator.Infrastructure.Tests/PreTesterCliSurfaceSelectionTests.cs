using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns its candidate tree; no processes or ambient mutation.
public sealed class PreTesterCliSurfaceSelectionTests
{
    private const string Handler = "src/Mcg.AgentOrchestrator.App/Cli/AttentionHandler.cs";

    [Theory]
    [InlineData(Handler)]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/Views/AttentionView.cs")]
    [InlineData("./src\\Mcg.AgentOrchestrator.App\\Cli\\DeletedHandler.cs")]
    public void SourceChangeDiscoversBehaviorTestsWithoutReferences(string path)
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        if (path == Handler)
        {
            var source = Path.Combine(fixture.Root, Handler);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "public class AttentionHandler {}"); // Modified source.
        }
        else Assert.False(File.Exists(Path.Combine(fixture.Root, path))); // Added/deleted paths need not exist.

        var selected = fixture.Select(path);

        Assert.Equal(["CliAttentionNextGoldenOutputTests", "CliAttentionNextReadOnlyWriterHeldTests"],
            selected.Selections.Select(item => item.TestClass));
        Assert.All(selected.Selections, item => Assert.Equal("Infrastructure.Cli.Tests", item.TestProject));
        Assert.All(selected.Provenance, item => Assert.Equal("cli-surface", item.Kind));
    }

    [Theory]
    [InlineData("src/Other.cs", Handler)] // Rename into CLI.
    [InlineData(Handler, "src/Other.cs")] // Rename out of CLI.
    [InlineData(Handler, "src/Mcg.AgentOrchestrator.App/Cli/NewHandler.cs")]
    public void EitherRecordedRenameSideTriggersAndKeepsEachReason(string oldPath, string newPath)
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        var result = fixture.Select(oldPath, newPath, oldPath);
        Assert.Equal(2, result.Selections.Count);
        var triggers = new[] { oldPath, newPath }.Where(path => path.Contains("/Cli/", StringComparison.Ordinal)).Distinct().ToArray();
        foreach (var selection in result.Selections)
            Assert.Equal(triggers, result.Provenance.Where(item => item.Class == selection.TestClass).Select(item => item.Path));
        Assert.Equal(result.Provenance.Count, result.Provenance.Distinct().Count());
    }

    [Fact]
    public void CandidateInventoryIncludesNewPartialAndInheritedTestsExcludesHelpersAndOtherProjects()
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        var before = fixture.Select(Handler);
        fixture.Write("NewBehavior.cs", "public partial class NewBehavior { [Fact] public void Works() {} }");
        fixture.Write("OtherPart.cs", "public partial class NewBehavior {}");
        fixture.Write("Helpers.cs", "public class Helper {} public abstract class BaseBehavior { [Fact] public void Works() {} } public class InheritedBehavior : BaseBehavior {}");
        var otherProject = Path.Combine(fixture.Root, "tests", "Other.Tests");
        Directory.CreateDirectory(otherProject);
        File.WriteAllText(Path.Combine(otherProject, "Other.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(otherProject, "OtherTests.cs"), "public class OtherTests { [Fact] public void Works() {} }");

        var after = fixture.Select(Handler, Handler);

        Assert.Equal(2, before.Selections.Count);
        Assert.Equal(["CliAttentionNextGoldenOutputTests", "CliAttentionNextReadOnlyWriterHeldTests", "InheritedBehavior", "NewBehavior"],
            after.Selections.Select(item => item.TestClass));
        Assert.Single(after.Selections.Where(item => item.TestClass == "NewBehavior"));
    }

    [Theory]
    [InlineData("src/Mcg.AgentOrchestrator.App/CliExtra/Handler.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Other.cs")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/readme.md")]
    [InlineData("src/Mcg.AgentOrchestrator.App/Cli/obj/Generated.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/Behavior.cs")]
    [InlineData("docs/Cli/Handler.cs")]
    public void UnrelatedChangesNeverResolveOrOpenCliInventory(string path)
    {
        using var fixture = new Fixture();
        File.Delete(fixture.ProjectPath);
        var result = PreTesterCliSurfaceSelections.Select(fixture.Root, [path], new());
        Assert.Empty(result.Selections);
        Assert.Empty(result.Provenance);
    }

    [Theory]
    [InlineData("missing-project")]
    [InlineData("malformed-project")]
    [InlineData("invalid-project-root")]
    [InlineData("empty-sources")]
    [InlineData("helper-only")]
    [InlineData("malformed-source")]
    [InlineData("unsupported-project")]
    public void InvalidCandidateInventoryFailsVisibly(string fault)
    {
        using var fixture = new Fixture();
        var settings = fixture.Settings;
        switch (fault)
        {
            case "missing-project": File.Delete(fixture.ProjectPath); break;
            case "malformed-project": File.WriteAllText(fixture.ProjectPath, "<Project>"); break;
            case "invalid-project-root": File.WriteAllText(fixture.ProjectPath, "<Other />"); break;
            case "helper-only": fixture.Write("Helper.cs", "public class Helper {}"); break;
            case "malformed-source": fixture.Write("Bad.cs", "public class Bad { [Fact] public void Broken("); break;
            case "unsupported-project": settings = new(); fixture.BehaviorTests(); break;
        }

        var error = Record.Exception(() => PreTesterCliSurfaceSelections.Select(fixture.Root, [Handler], settings));

        Assert.NotNull(error);
        Assert.True(error is IOException or InvalidDataException or System.Xml.XmlException, error.ToString());
    }

    [Fact]
    public void UnreadableSourceIsNotEmptySuccess()
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        using var held = new FileStream(Path.Combine(fixture.ProjectDirectory, "CliAttentionNextGoldenOutputTests.cs"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => fixture.Select(Handler));
    }

    [Fact]
    public void CompositeDeduplicatesAndPreservesIndependentGuardProvenance()
    {
        using var fixture = new Fixture();
        fixture.Write("ObservedTests.cs", """
            public class ObservedTests { [Fact] public void Reads() {
                File.ReadAllText("src/Mcg.AgentOrchestrator.App/Cli/AttentionHandler.cs");
            } }
            """);
        var lines = new List<string>();
        var guards = PreTesterGuardSelections.Create(new(), fixture.Root, [Handler], "base", lines.Add, fixture.Settings);
        var normalized = guards.NormalizeWithNotRun(fixture.Settings);
        var distinct = guards.RetainStartedSelections(normalized.Selections.Concat(normalized.Selections), null,
            ConductorDriver.FormatFindingEvidenceSelection);
        Assert.Empty(normalized.NotRun);
        Assert.Single(distinct);
        Assert.Contains(lines, line => line.Contains("kind=read path=" + Handler, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("kind=cli-surface path=" + Handler, StringComparison.Ordinal));
        Assert.True(guards.EveryFailureIsInsideChanges(["ObservedTests.Reads"], _ => false));
    }

    [Fact]
    public void NormalizationRefusalRetainsNotRunObligation()
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        var guards = PreTesterGuardSelections.Create(new(), fixture.Root, [Handler], "base", _ => {}, fixture.Settings);
        var normalized = guards.NormalizeWithNotRun(new());
        Assert.Empty(normalized.Selections);
        Assert.Equal(["CliAttentionNextGoldenOutputTests", "CliAttentionNextReadOnlyWriterHeldTests"], normalized.NotRun);
    }

    [Fact]
    public void IndividualFilterBeyondExistingBoundRemainsExplicitlyNotRun()
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        var oversized = "Behavior" + new string('X', GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        fixture.Write("Oversized.cs", "public class " + oversized + " { [Fact] public void Output() {} }");
        var guards = PreTesterGuardSelections.Create(new(), fixture.Root, [Handler], "base", _ => {}, fixture.Settings);
        Assert.Equal(3, guards.Selections.Count);

        var normalized = guards.NormalizeWithNotRun(fixture.Settings);

        Assert.Equal(2, normalized.Selections.Count);
        Assert.Equal(oversized, Assert.Single(normalized.NotRun));
        Assert.DoesNotContain(normalized.Selections, selection => selection.TestClass == oversized);
    }

    [Fact]
    public void LegacyStartedOverlapRetainsBothRecordedProjectSelections()
    {
        using var fixture = new Fixture();
        fixture.BehaviorTests();
        FindingEvidenceSelection[] original = [new("Infrastructure.Tests", "CliAttentionNextGoldenOutputTests"),
            new("Infrastructure.Cli.Tests", "CliAttentionNextGoldenOutputTests")];
        var names = original.Select(ConductorDriver.FormatFindingEvidenceSelection).ToArray();
        var guards = PreTesterGuardSelections.Create(new(), fixture.Root, [Handler], "base", _ => {},
            new(), priorSelections: names);

        Assert.Equal(original, guards.RetainStartedSelections(original, names, ConductorDriver.FormatFindingEvidenceSelection));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        public string ProjectPath => Path.Combine(Root, PreTesterCliSurfaceSelections.Project);
        public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;
        public AcceptanceGateEngineSettings Settings { get; } = new()
        {
            MtpInvocations = [new AcceptanceMtpInvocation { Project = PreTesterCliSurfaceSelections.Project }]
        };
        public Fixture()
        {
            Directory.CreateDirectory(ProjectDirectory);
            File.WriteAllText(ProjectPath, "<Project />");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(ProjectDirectory)!, "Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"),
                "<Project><ItemGroup><Compile Remove=\"Cli/**/*.cs\" /></ItemGroup></Project>");
        }
        public void BehaviorTests()
        {
            Write("CliAttentionNextGoldenOutputTests.cs", "public class CliAttentionNextGoldenOutputTests { [Fact] public void Output() {} }");
            Write("CliAttentionNextReadOnlyWriterHeldTests.cs", "public class CliAttentionNextReadOnlyWriterHeldTests { [Fact] public void Output() {} }");
        }
        public void Write(string file, string source) => File.WriteAllText(Path.Combine(ProjectDirectory, file), source);
        public (IReadOnlyList<FindingEvidenceSelection> Selections, IReadOnlyList<PreTesterChangedFileGuardClasses.Provenance> Provenance)
            Select(params string[] paths) => PreTesterCliSurfaceSelections.Select(Root, paths, Settings);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
