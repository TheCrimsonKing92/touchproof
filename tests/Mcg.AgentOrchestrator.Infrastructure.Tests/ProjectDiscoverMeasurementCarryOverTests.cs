using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its fixture, registry and data; external commands use fakes.
public sealed class ProjectDiscoverMeasurementCarryOverTests
{
    [Theory]
    [InlineData("build", "test", "test", true, true)]
    [InlineData("changed build", "test", "test", false, true)]
    [InlineData("build", "changed test", "test", true, false)]
    [InlineData("changed build", "changed test", "test", false, false)]
    [InlineData("BUILD", "test", "test", false, true)]
    [InlineData("build", "test ", "test", true, false)]
    [InlineData("build", null, null, true, true)]
    [InlineData("build", null, "test", true, false)]
    [InlineData("build", "test", null, true, false)]
    public void Apply_CommandValues_RetainsEachDurationIndependently(
        string build, string? test, string? storedTest, bool keepBuild, bool keepTest)
    {
        var stored = Model("unit", "build", storedTest);
        var observed = Measurement("unit");
        stored = stored with { Measurements = [observed] };
        // A changed source location/confidence alone must not invalidate an observation.
        var fresh = Model("unit", build, test, line: 9);

        var result = StoredMeasurementCarryOver.Apply(fresh, stored);

        Assert.Equal(fresh, result with { Measurements = fresh.Measurements });
        if (!keepBuild && !keepTest)
            Assert.Empty(result.Measurements);
        else
        {
            var kept = Assert.Single(result.Measurements);
            Assert.Equal("unit", kept.UnitId);
            Assert.Equal(keepBuild ? observed.BuildSeconds : null, kept.BuildSeconds);
            Assert.Equal(keepTest ? observed.TestSeconds : null, kept.TestSeconds);
        }
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("case-changed")]
    [InlineData("stored-command-missing")]
    [InlineData("fresh-command-missing")]
    public void Apply_UnitOrCommandMissing_DropsObservation(string scenario)
    {
        var stored = Model("unit", "build", "test") with { Measurements = [Measurement("unit")] };
        var fresh = Model("unit", "build", "test");
        if (scenario == "gone") fresh = fresh with { Units = [] };
        if (scenario == "case-changed") fresh = Model("UNIT", "build", "test");
        if (scenario == "stored-command-missing") stored = stored with { Commands = [] };
        if (scenario == "fresh-command-missing") fresh = fresh with { Commands = [] };

        Assert.Empty(StoredMeasurementCarryOver.Apply(fresh, stored).Measurements);
    }

    [Fact]
    public void Apply_NullStoredEntries_KeepsEligibleObservation()
    {
        var fresh = Model("unit", "build", "test");
        var observed = Measurement("unit");
        var stored = fresh with { Commands = [null!, .. fresh.Commands], Measurements = [null!, observed] };

        Assert.Equal(observed, Assert.Single(StoredMeasurementCarryOver.Apply(fresh, stored).Measurements));
    }

    [Fact]
    public void Summary_DuplicateObservations_CountsUnitsAndMissingTestDurations()
    {
        var model = Model("unit", "build", "test") with
        {
            Measurements = [new("unit", Measurement("unit").BuildSeconds, null),
                new("unit", Measurement("unit").BuildSeconds, null)]
        };

        Assert.Equal("Kept 1 unit measurements; 1 test units have no measured test duration",
            StoredMeasurementCarryOver.SummaryLine(model));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rediscover_TestCommandChange_PreservesEligibleDurationsAndTimeoutQuestion(
        bool changeTestCommand)
    {
        using var fixture = new Fixture();
        var measurer = new RecordingUnitCommandMeasurer();
        var (measured, _) = fixture.Discover("all", measurer);
        Assert.Equal(2, measurer.Calls.Count(call => call.Kind == UnitCommandKinds.Test));
        Assert.NotEmpty(measured.Measurements);
        var testUnit = "tests/AnswerKey.App.Tests/AnswerKey.App.Tests.csproj";
        var original = measured.Commands.Single(command => command.UnitId == testUnit);
        Assert.NotNull(original.TestCommand);
        if (changeTestCommand)
        {
            var projectPath = Path.Combine(fixture.Source.Root, testUnit);
            var xml = File.ReadAllText(projectPath);
            Assert.Contains("<IsTestProject>true</IsTestProject>", xml);
            File.WriteAllText(projectPath, xml.Replace("<IsTestProject>true</IsTestProject>",
                "<IsTestProject>true</IsTestProject>\n    <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>",
                StringComparison.Ordinal));
        }

        var (rediscovered, lines) = fixture.Discover();

        var current = rediscovered.Commands.Single(command => command.UnitId == testUnit);
        Assert.Equal(original.BuildCommand.Value, current.BuildCommand.Value);
        Assert.Equal(changeTestCommand, original.TestCommand.Value != current.TestCommand!.Value);
        var expected = measured.Measurements.Select(measurement =>
            changeTestCommand && measurement.UnitId == testUnit
                ? new UnitMeasurement(testUnit, measurement.BuildSeconds, null) : measurement).ToArray();
        Assert.Equal(expected, rediscovered.Measurements);
        Assert.Equal($"Kept {measured.Measurements.Count} unit measurements; {(changeTestCommand ? 1 : 0)} test units have no measured test duration", lines[2]);
        var settings = LearnedEngineSettingsDeriver.Derive(rediscovered);
        if (changeTestCommand)
        {
            var question = Assert.Single(settings.OwnerQuestions.Where(question =>
                question.FactKey == OwnerAnswerValidator.DefaultTimeoutKey));
            Assert.Contains(testUnit, question.Question);
        }
        else
            Assert.DoesNotContain(settings.OwnerQuestions, question =>
                question.FactKey == OwnerAnswerValidator.DefaultTimeoutKey);
    }

    [Fact]
    public void Rediscover_GoneUnit_DropsItsStoredMeasurement()
    {
        using var fixture = new Fixture();
        var (measured, _) = fixture.Discover("all");
        Assert.NotEmpty(measured.Measurements);
        var gone = Model("gone", "build", "test");
        fixture.Write(measured with
        {
            Units = [.. measured.Units, .. gone.Units],
            Commands = [.. measured.Commands, .. gone.Commands],
            Measurements = [.. measured.Measurements, Measurement("gone")]
        });

        var (rediscovered, lines) = fixture.Discover();

        Assert.DoesNotContain(rediscovered.Units, unit => unit.Id == "gone");
        Assert.Equal(measured.Measurements, rediscovered.Measurements);
        Assert.Equal($"Kept {measured.Measurements.Count} unit measurements; 0 test units have no measured test duration", lines[2]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("schema-invalid")]
    [InlineData("null-list")]
    [InlineData("null-model")]
    [InlineData("null-entry")]
    [InlineData("invalid-measurement")]
    public void Rediscover_UnavailableStoredModel_WritesEmptyMeasurements(string scenario)
    {
        using var fixture = new Fixture();
        if (scenario != "missing")
        {
            var model = Model("unit", "build", "test") with { Measurements = [Measurement("unit")] };
            var json = JsonNode.Parse(ProjectModelJson.Serialize(model))!;
            switch (scenario)
            {
                case "schema-invalid": json["schemaVersion"] = -1; break;
                case "null-list": json["commands"] = null; break;
                case "null-entry": json["measurements"] = new JsonArray((JsonNode?)null); break;
                case "invalid-measurement":
                    json["measurements"]![0]!["buildSeconds"] = null;
                    json["measurements"]![0]!["testSeconds"] = null;
                    break;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.ModelPath)!);
            File.WriteAllText(fixture.ModelPath, scenario switch
            {
                "malformed" => "{broken",
                "null-model" => "null",
                _ => json.ToJsonString()
            });
        }

        var (rediscovered, lines) = fixture.Discover();

        Assert.Empty(rediscovered.Measurements);
        Assert.Equal(2, rediscovered.Commands.Count(command => command.TestCommand is not null));
        Assert.Equal("Kept 0 unit measurements; 2 test units have no measured test duration", lines[2]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(fixture.ModelPath)!, ".project-model-*.tmp"));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("build")]
    [InlineData("test")]
    public void Remeasure_NewDurations_ReplacesStoredMeasurementsWithoutKeptLine(string kind)
    {
        using var fixture = new Fixture();
        var (original, _) = fixture.Discover("all");
        Assert.NotEmpty(original.Measurements);
        var measurer = new RecordingUnitCommandMeasurer
        {
            Result = (_, commandKind) => UnitCommandMeasurementResult.Succeeded(commandKind == UnitCommandKinds.Build ? 25 : 6)
        };

        var (remeasured, lines) = fixture.Discover(kind, measurer);

        Assert.NotEmpty(measurer.Calls);
        var expected = original.Measurements.Where(measurement => kind != "test" || measurement.TestSeconds is not null);
        Assert.Equal(expected.Select(measurement => measurement.UnitId),
            remeasured.Measurements.Select(measurement => measurement.UnitId));
        Assert.All(remeasured.Measurements, measurement =>
        {
            if (kind == "test") Assert.Null(measurement.BuildSeconds);
            else Assert.Equal(25, measurement.BuildSeconds!.Value);
            if (kind != "build" && original.Measurements.Single(item => item.UnitId == measurement.UnitId).TestSeconds is not null)
                Assert.Equal(6, measurement.TestSeconds!.Value);
            else Assert.Null(measurement.TestSeconds);
        });
        Assert.DoesNotContain(lines, line => line.StartsWith("Kept ", StringComparison.Ordinal));
    }

    private static ProjectModel Model(string id, string build, string? test, int line = 1)
    {
        var source = new FactSource("project.csproj", line);
        ProjectFact<string> Command(string value) => new(value, source,
            line == 1 ? FactConfidence.High : FactConfidence.Medium);
        return new(ProjectModel.CurrentSchemaVersion, ".",
            [new(id, Command("unit"), Command("project.csproj"), new(true, source, FactConfidence.High))],
            [], [], [], [new(id, Command(build), test is null ? null : Command(test))], [], [], []);
    }

    private static UnitMeasurement Measurement(string id) => new(id,
        new(12.5, new FactSource(measurementReference: "build-receipt"), FactConfidence.High),
        new(3.25, new FactSource(measurementReference: "test-receipt"), FactConfidence.High));

    private sealed class Fixture : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("solution");
        private readonly FakeCrosswalkProcess crosswalk = new("dotnet");
        public ProjectOnboardingFixture Source { get; } = new("answer-key-repository");
        private OrchestratorProjectRegistry Registry { get; }
        public string ModelPath { get; }

        public Fixture()
        {
            Registry = new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            Registry.CreateProject("sample", Source.Root);
            ModelPath = Path.Combine(Registry.GetRequiredProject("sample").ResolveWorkspace().OrchestratorDirectory,
                "project-model.json");
        }

        public void Write(ProjectModel model) => File.WriteAllText(ModelPath, ProjectModelJson.Serialize(model));

        public (ProjectModel Model, string[] Lines) Discover(string? kind = null, IUnitCommandMeasurer? measurer = null)
        {
            using var output = new StringWriter();
            string[] parts = kind is null ? ["project", "discover", "sample"]
                : ["project", "discover", "sample", "--measure", kind];
            var crosswalkCalls = crosswalk.Calls.Count;
            Assert.Equal(0, ProjectCliCommand.Execute(parts, Registry, home.Root, null, output,
                measurerFactory: () => kind is null
                    ? throw new InvalidOperationException("Non-measuring discovery constructed a measurer")
                    : measurer ?? new RecordingUnitCommandMeasurer(),
                pullRequestProbeFactory: () => new RequiredProbe(), crosswalkReaderFactory: () => crosswalk.Reader));
            Assert.Equal(crosswalkCalls + 1, crosswalk.Calls.Count);
            return (ProjectModelJson.Deserialize(File.ReadAllText(ModelPath)),
                output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Dispose()
        {
            crosswalk.Dispose();
            Source.Dispose();
            home.Dispose();
        }
    }

    private sealed class RequiredProbe : IPullRequestRequirementProbe
    {
        public PullRequestRequirement Probe(string repositoryRoot, string branch) => PullRequestRequirement.Required("fixture");
    }
}
