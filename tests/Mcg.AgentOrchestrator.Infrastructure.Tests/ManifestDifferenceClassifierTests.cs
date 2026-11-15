using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure classification and owned fixture roots with fake external services.
public sealed class ManifestDifferenceClassifierTests
{
    [Fact]
    public void Classify_InstalledLacksOnlyEnvironmentContract_IsNewSectionsOnly()
    {
        const string installed = """{"version":1,"checks":[{"name":"build","arguments":["a"]}]}""";
        const string learned = """{"version":1,"checks":[{"name":"build","arguments":["a"]}],"environmentContract":{}}""";

        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(installed, learned));

        Assert.Equal(ManifestDifferenceKind.NewSectionsOnly, result.Kind);
        Assert.Equal(["environmentContract"], result.SectionsAdded);
        Assert.Empty(result.SectionsMissing);
        Assert.Empty(result.SectionsChanged);
        AssertNoCheckDifferences(result);
    }

    [Fact]
    public void Classify_ChecksAddedMissingAndChanged_ReportsExactNames()
    {
        const string installed = """
            {"aMissing":1,"ZMissing":1,"aChanged":1,"ZChanged":1,"checks":[
              {"name":"aGone"},{"name":"ZGone"},
              {"name":"aEdit","arguments":["old"]},{"name":"ZEdit","timeout":1},
              {"name":"same","nested":{"a":1,"b":2}}]}
            """;
        const string learned = """
            {"aAdded":1,"ZAdded":1,"aChanged":2,"ZChanged":2,"checks":[
              {"name":"aNew"},{"name":"ZNew"},
              {"name":"aEdit","arguments":["new"]},{"name":"ZEdit","timeout":2},
              {"nested":{"b":2.0,"a":1.0},"name":"same"}]}
            """;

        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(installed, learned));

        Assert.Equal(ManifestDifferenceKind.ChecksChanged, result.Kind);
        Assert.Equal(["ZAdded", "aAdded"], result.SectionsAdded);
        Assert.Equal(["ZMissing", "aMissing"], result.SectionsMissing);
        Assert.Equal(["ZChanged", "aChanged", "checks"], result.SectionsChanged);
        Assert.Equal(["ZNew", "aNew"], result.ChecksAdded);
        Assert.Equal(["ZGone", "aGone"], result.ChecksMissing);
        Assert.Equal(["ZEdit", "aEdit"], result.ChecksChanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Classify_OneCheckArgumentChanged_IsChecksChangedNotNewSectionsOnly(bool addSection)
    {
        const string installed = """{"checks":[{"name":"build","arguments":["a","b"]}]}""";
        var learned = addSection
            ? """{"checks":[{"name":"build","arguments":["a","c"]}],"environmentContract":{}}"""
            : """{"checks":[{"name":"build","arguments":["a","c"]}]}""";

        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(installed, learned));

        Assert.Equal(ManifestDifferenceKind.ChecksChanged, result.Kind);
        Assert.NotEqual(ManifestDifferenceKind.NewSectionsOnly, result.Kind);
        Assert.Equal(["build"], result.ChecksChanged);
        Assert.Empty(result.ChecksAdded);
        Assert.Empty(result.ChecksMissing);
        Assert.Equal(["checks"], result.SectionsChanged);
        Assert.Equal(addSection ? new[] { "environmentContract" } : [], result.SectionsAdded);
        Assert.Empty(result.SectionsMissing);
    }

    [Fact]
    public void Classify_EngineValueOnly_IsOtherWithEngineChanged()
    {
        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(
            """{"engine":{"timeout":1},"checks":[]}""",
            """{"engine":{"timeout":2},"checks":[]}"""));

        Assert.Equal(ManifestDifferenceKind.Other, result.Kind);
        Assert.Equal(["engine"], result.SectionsChanged);
        Assert.Empty(result.SectionsAdded);
        Assert.Empty(result.SectionsMissing);
        AssertNoCheckDifferences(result);
    }

    [Fact]
    public void Classify_StructurallyEqualText_IsOtherWithEmptyLists()
    {
        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(
            """{"version":1,"checks":[{"name":"build","arguments":[1,"a"]}]}""",
            """{ "checks": [ { "arguments": [1.0,"a"], "name": "build" } ], "version": 1.0 }"""));

        Assert.Equal(ManifestDifferenceKind.Other, result.Kind);
        Assert.Empty(result.SectionsAdded);
        Assert.Empty(result.SectionsMissing);
        Assert.Empty(result.SectionsChanged);
        AssertNoCheckDifferences(result);
    }

    [Fact]
    public void Classify_ReorderedChecks_IsOtherWithChecksSectionChanged()
    {
        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(
            """{"checks":[{"name":"a"},{"name":"b"}]}""",
            """{"checks":[{"name":"b"},{"name":"a"}]}"""));

        Assert.Equal(ManifestDifferenceKind.Other, result.Kind);
        Assert.Equal(["checks"], result.SectionsChanged);
        Assert.Empty(result.SectionsAdded);
        Assert.Empty(result.SectionsMissing);
        AssertNoCheckDifferences(result);
    }

    [Theory]
    [InlineData("{}", "{\"checks\":[]}", ManifestDifferenceKind.NewSectionsOnly)]
    [InlineData("{}", "{\"checks\":[{\"name\":\"a\"}]}", ManifestDifferenceKind.ChecksChanged)]
    [InlineData("{\"checks\":[{\"name\":\"a\"}]}", "{}", ManifestDifferenceKind.ChecksChanged)]
    public void Classify_AbsentChecks_ComparesAsEmptyCollection(
        string installed, string learned, ManifestDifferenceKind kind)
    {
        var result = Assert.IsType<ManifestDifference>(ManifestDifferenceClassifier.Classify(installed, learned));

        Assert.Equal(kind, result.Kind);
        Assert.Equal(learned == "{}" ? new[] { "checks" } : [], result.SectionsMissing);
        Assert.Equal(installed == "{}" ? new[] { "checks" } : [], result.SectionsAdded);
        Assert.Empty(result.SectionsChanged);
        Assert.Equal(learned.Contains("\"a\"", StringComparison.Ordinal) ? new[] { "a" } : [], result.ChecksAdded);
        Assert.Equal(installed.Contains("\"a\"", StringComparison.Ordinal) ? new[] { "a" } : [], result.ChecksMissing);
        Assert.Empty(result.ChecksChanged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("{}!")]
    [InlineData("\uFEFF{}")]
    [InlineData("{\"version\":1,\"version\":2}")]
    [InlineData("{\"section\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"section\":[{\"x\":1,\"x\":2}]}")]
    [InlineData("{\"checks\":{}}")]
    [InlineData("{\"checks\":null}")]
    [InlineData("{\"checks\":[1]}")]
    [InlineData("{\"checks\":[{}]}")]
    [InlineData("{\"checks\":[{\"name\":\"\"}]}")]
    [InlineData("{\"checks\":[{\"name\":1}]}")]
    [InlineData("{\"checks\":[{\"name\":\"a\"},{\"name\":\"a\"}]}")]
    public void Classify_UnclassifiableInput_ReturnsNullWithoutThrowing(string? invalid)
    {
        Assert.Null(ManifestDifferenceClassifier.Classify(invalid, "{}"));
        Assert.Null(ManifestDifferenceClassifier.Classify("{}", invalid));
    }

    [Fact]
    public void Classify_LoneSurrogate_ReturnsNullWithoutThrowing()
    {
        var invalid = new string('\ud800', 1);

        Assert.Null(ManifestDifferenceClassifier.Classify(invalid, "{}"));
        Assert.Null(ManifestDifferenceClassifier.Classify("{}", invalid));
    }

    [Fact]
    public void Check_InstalledLacksOnlyEnvironmentContract_PrintsFullClassifiedOutput()
    {
        using var fixture = new Fixture();
        var learned = File.ReadAllBytes(fixture.ManifestPath);
        var text = Encoding.UTF8.GetString(learned);
        var sectionStart = text.LastIndexOf(",\n  \"environmentContract\":", StringComparison.Ordinal);
        Assert.True(sectionStart > 0, "emitted manifest must end with environmentContract");
        var installedText = text[..sectionStart] + "\n}\n";
        var installed = Encoding.UTF8.GetBytes(installedText);
        File.WriteAllBytes(fixture.ManifestPath, installed);
        var offset = Encoding.UTF8.GetByteCount(text[..sectionStart]);
        var line = 1 + text[..sectionStart].Count(character => character == '\n');
        var installedLine = installedText.Split('\n')[line - 1];
        var learnedLine = text.Split('\n')[line - 1];

        var (exit, lines) = fixture.Check();

        Assert.Equal(1, exit);
        Assert.Equal(new[] { $"Differs sample at byte offset {offset} line {line}",
            $"  installed: {installedLine}", $"  learned: {learnedLine}",
            "  kind: NewSectionsOnly", "  sections: added=environmentContract missing=- changed=-",
            "  checks: added=- missing=- changed=-" }, lines);
        Assert.Equal(installed, File.ReadAllBytes(fixture.ManifestPath));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("[]")]
    public void Check_InstalledNotJsonObject_PrintsOnlyLocationAndExcerpts(string installedText)
    {
        using var fixture = new Fixture();
        var learnedLine = File.ReadAllText(fixture.ManifestPath).Split('\n')[0];
        File.WriteAllText(fixture.ManifestPath, installedText, new UTF8Encoding(false));

        var (exit, lines) = fixture.Check();

        Assert.Equal(1, exit);
        Assert.Equal(new[] { "Differs sample at byte offset 0 line 1",
            $"  installed: {installedText}", $"  learned: {learnedLine}" }, lines);
    }

    private static void AssertNoCheckDifferences(ManifestDifference result)
    {
        Assert.Empty(result.ChecksAdded);
        Assert.Empty(result.ChecksMissing);
        Assert.Empty(result.ChecksChanged);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture scratch = new("no-solution");
        private readonly ProjectOnboardingFixture source = new("answer-key-repository");
        private readonly FakeCrosswalkProcess crosswalk = new("dotnet");
        private readonly OrchestratorProjectRegistry registry;
        public string ManifestPath { get; }

        public Fixture()
        {
            registry = new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            registry.CreateProject("sample", source.Root);
            ManifestPath = Path.Combine(registry.GetRequiredProject("sample").ResolveWorkspace().OrchestratorDirectory,
                "acceptance-manifest.json");
            Assert.Equal(0, Execute(["project", "discover", "sample", "--measure", "all"]).Exit);
            foreach (var (key, value) in new[] { ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
            var emitted = Path.Combine(scratch.Root, "sample.json");
            Assert.Equal(0, Execute(["project", "emit-manifest", "sample", "--out", emitted]).Exit);
            Assert.Equal(0, Execute(["project", "install-manifest", "sample", "--from", emitted]).Exit);
        }

        private (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output,
                measurerFactory: () => new RecordingUnitCommandMeasurer(),
                pullRequestProbeFactory: () => new RequiredProbe(), crosswalkReaderFactory: () => crosswalk.Reader);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public (int Exit, string[] Lines) Check()
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(["project", "check-manifest", "sample"], registry, home.Root, null,
                output, measurerFactory: () => throw new InvalidOperationException("check-manifest must not measure"),
                crosswalkReaderFactory: () => crosswalk.Reader);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Dispose()
        {
            crosswalk.Dispose();
            source.Dispose();
            scratch.Dispose();
            home.Dispose();
        }
    }

    private sealed class RequiredProbe : IPullRequestRequirementProbe
    {
        public PullRequestRequirement Probe(string repositoryRoot, string branch) => PullRequestRequirement.Required("fixture");
    }
}
