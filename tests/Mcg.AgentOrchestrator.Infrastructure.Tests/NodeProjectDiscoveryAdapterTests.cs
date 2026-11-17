using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixtures own their directories; discovery invokes no processes or shared state.
public sealed class NodeProjectDiscoveryAdapterTests
{
    [Fact]
    public void Discover_HighConfidenceVitest_DerivesOneUnitWithNpxVitestRunCommand()
    {
        using var fixture = new Fixture();

        var model = fixture.Discover();

        var unit = Assert.Single(model.Units);
        Assert.Equal(".", unit.Id);
        Assert.Equal(".", unit.Name.Value);
        Assert.Equal(".", unit.Location.Value);
        Assert.False(unit.IsTest.Value);
        var commands = Assert.Single(model.Commands);
        Assert.Equal(unit.Id, commands.UnitId);
        Assert.Null(commands.BuildCommand);
        var test = Assert.IsType<ProjectFact<string>>(commands.TestCommand);
        Assert.Equal("npx vitest run", test.Value);
        Assert.Equal(FactConfidence.High, test.Confidence);
        Assert.Equal("package.json", test.Source.Path);
        Assert.Equal(9, test.Source.Line);
        Assert.Equal(Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-test-framework").Value.Source,
            test.Source);
    }

    [Theory]
    [InlineData("{\"devDependencies\":{\"jest\":\"30\"}}")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\",\"jest\":\"30\"}}")]
    [InlineData("{}")]
    public void Discover_NonVitestFramework_DerivesNoUnitOrCommand(string package)
    {
        using var fixture = new Fixture(package);

        var model = fixture.Discover();

        Assert.Empty(model.Units);
        Assert.Empty(model.Commands);
    }

    [Fact]
    public void Discover_MeasureTestOnly_MeasuresVitestOnceWithoutBuild() =>
        AssertTestMeasurement(UnitCommandKinds.Test);

    [Fact]
    public void Discover_MeasureAll_MeasuresVitestOnceWithoutBuild() =>
        AssertTestMeasurement(UnitCommandKinds.All);

    private static void AssertTestMeasurement(UnitCommandKinds kinds)
    {
        using var fixture = new Fixture();
        var measurer = new RecordingMeasurer();

        var model = new NodeProjectDiscoveryAdapter().Discover(fixture.Root, measurer: measurer, measuredKinds: kinds);

        var call = Assert.Single(measurer.Calls);
        Assert.Equal((fixture.Root, ".", UnitCommandKinds.Test, "npx vitest run"), call);
        var measurement = Assert.Single(model.Measurements);
        Assert.Equal(Assert.Single(model.Units).Id, measurement.UnitId);
        Assert.Null(measurement.BuildSeconds);
        var seconds = Assert.IsType<ProjectFact<double>>(measurement.TestSeconds);
        Assert.Equal(32d, seconds.Value);
        Assert.Equal(FactConfidence.High, seconds.Confidence);
        Assert.Equal("measurement:project-discover/./test", seconds.Source.MeasurementReference);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("commands/", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_MeasureBuildOnly_SkipsAbsentBuildWithoutQuestions()
    {
        using var fixture = new Fixture();

        var model = new NodeProjectDiscoveryAdapter().Discover(fixture.Root,
            measurer: new RejectingMeasurer(), measuredKinds: UnitCommandKinds.Build);

        Assert.Single(model.Commands);
        Assert.Empty(model.Measurements);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.StartsWith("commands/", StringComparison.Ordinal));
    }

    [Fact]
    public void LearnedManifest_VitestUnit_LeavesNodeEmissionAndComparisonUnchanged()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();
        Assert.Single(model.Units);
        Assert.Single(model.Commands);
        var baseline = model with { Units = [], Commands = [], Measurements = [] };

        var previous = LearnedAcceptanceManifest.Build(baseline, Fixture.Answers());
        var current = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Empty(previous.Blockers);
        Assert.Empty(current.Blockers);
        Assert.NotNull(previous.Json);
        Assert.NotNull(current.Json);
        Assert.Equal(Encoding.UTF8.GetBytes(previous.Json), Encoding.UTF8.GetBytes(current.Json));
        var expectedChecks = JsonNode.Parse("""
            [
              {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
              {"name":"npx tsc --noEmit","type":"command","command":"npx","arguments":["tsc","--noEmit"]},
              {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expectedChecks, JsonNode.Parse(current.Json)!["checks"]));
        var previousRows = ManifestCheckComparer.Compare(baseline, previous.Json);
        var currentRows = ManifestCheckComparer.Compare(model, current.Json);
        Assert.Equal(3, currentRows.Count);
        Assert.All(currentRows, row => Assert.Equal(ManifestCheckResultKind.Match, row.Kind));
        Assert.Equal(previousRows.ToArray(), currentRows.ToArray());
        var previousBlockers = LearnedAcceptanceManifest.Build(baseline, []).Blockers;
        var currentBlockers = LearnedAcceptanceManifest.Build(model, []).Blockers;
        Assert.NotEmpty(previousBlockers);
        Assert.Equal(previousBlockers.Select(blocker => blocker.FactKey).Order(StringComparer.Ordinal),
            currentBlockers.Select(blocker => blocker.FactKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Discover_NpmVitestTypeScript_RecordsConfidentSourcedFacts()
    {
        using var fixture = new Fixture();

        var model = fixture.Discover();

        Assert.Equal(ProjectStack.Node, model.Stack);
        Assert.Equal(".", model.RepositoryRoot);
        AssertFact(model, "node-package-manager", "npm", "package-lock.json", 1);
        AssertFact(model, "node-test-framework", "vitest", "package.json", 9);
        AssertFact(model, "node-typescript-config", "tsconfig.json", "tsconfig.json", 1);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey is
            "environment/node-package-manager" or "environment/node-test-framework");
    }

    [Theory]
    [InlineData("{\"devDependencies\":{\"jest\":\"30\"}}", "jest")]
    [InlineData("{}", "undetermined")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\",\"jest\":\"30\"}}", "ambiguous")]
    [InlineData("{\"dependencies\":{\"vitest\":\"4\"}}", "undetermined")]
    [InlineData("{\"scripts\":{\"test\":\"vitest run\"}}", "undetermined")]
    public void Discover_UnsupportedFramework_AsksWithoutGuessing(string package, string expected)
    {
        using var fixture = new Fixture(package);

        var model = fixture.Discover();

        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-test-framework").Value;
        Assert.Equal(expected, fact.Value);
        Assert.Equal(expected == "jest" ? FactConfidence.High : FactConfidence.Low, fact.Confidence);
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "environment/node-test-framework");
        Assert.Equal("package.json", question.Source.Path);
        Assert.DoesNotContain(model.EnvironmentNeeds, need => need.Name == "node-test-framework" && need.Value.Value == "vitest");
    }

    [Theory]
    [InlineData("", "undetermined")]
    [InlineData("package-lock.json|pnpm-lock.yaml", "ambiguous")]
    [InlineData("pnpm-lock.yaml|yarn.lock", "ambiguous")]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    public void Discover_UnsupportedLockfiles_AsksWithoutGuessing(string files, string expected)
    {
        using var fixture = new Fixture(lockfiles: files.Split('|', StringSplitOptions.RemoveEmptyEntries));
        fixture.Write("package.json", """{"packageManager":"npm@11","devDependencies":{"vitest":"4"}}""");

        var model = fixture.Discover();

        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-package-manager").Value;
        Assert.Equal(expected, fact.Value);
        Assert.Equal(expected is "pnpm" or "yarn" ? FactConfidence.High : FactConfidence.Low, fact.Confidence);
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/node-package-manager");
        Assert.NotEqual("npm", fact.Value);
        if (expected is "pnpm" or "yarn")
        {
            Assert.Equal(files, fact.Source.Path);
            Assert.Equal(1, fact.Source.Line);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discover_AnyCheckout_RecordsRegistryHazardAndEnvironmentQuestion(bool installed)
    {
        using var fixture = new Fixture();
        if (installed)
            fixture.Write("node_modules/installed.txt", "already installed");

        var model = fixture.Discover();

        var hazard = Assert.Single(model.Hazards);
        Assert.Equal("node-install-needs-registry-network", hazard.Name);
        Assert.Equal(".", hazard.UnitId);
        Assert.Equal("npm-registry", hazard.IsolationKey.Value);
        Assert.Equal(FactConfidence.High, hazard.IsolationKey.Confidence);
        Assert.Equal("package-lock.json", hazard.IsolationKey.Source.Path);
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "environment/node-registry-access");
        Assert.Contains("worker and gate", question.Question, StringComparison.Ordinal);
        Assert.Contains("configured npm registry", question.Question, StringComparison.Ordinal);
        Assert.Contains("no node_modules", question.Question, StringComparison.Ordinal);
    }

    [Fact]
    public void Discover_NoTypeScriptConfig_RecordsAbsenceWithoutQuestion()
    {
        using var fixture = new Fixture(hasTsconfig: false);

        var model = fixture.Discover();

        AssertFact(model, "node-typescript-config",
            "absent: no TypeScript type-check, because tsconfig.json is absent at the root", "tsconfig.json", 1);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.Contains("typescript", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("[]")]
    [InlineData("{\"devDependencies\":[]}")]
    public void Discover_MalformedPackage_ReportsPathAndLeavesFrameworkUnresolved(string package)
    {
        using var fixture = new Fixture(package);

        var model = fixture.Discover();

        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == "discovery/files/package.json/parse");
        Assert.Equal("package.json", question.Source.Path);
        Assert.Equal("undetermined", Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-test-framework").Value.Value);
        Assert.Empty(NodeVerificationChecks.For(model).Checks);
    }

    [Fact]
    public void Discover_UnreadablePackage_ReportsReadQuestion()
    {
        using var fixture = new Fixture();
        using var locked = new FileStream(Path.Combine(fixture.Root, "package.json"), FileMode.Open, FileAccess.Read, FileShare.None);

        var model = fixture.Discover();

        Assert.Contains(model.OwnerQuestions, item => item.FactKey == "discovery/files/package.json/read" && item.Source.Path == "package.json");
        Assert.Empty(NodeVerificationChecks.For(model).Checks);
    }

    [Fact]
    public void Discover_FileOnlyContract_DoesNotMeasureAndReadsIntegrationReference()
    {
        using var fixture = new Fixture();
        fixture.Write(".git/refs/remotes/origin/HEAD", "ref: refs/remotes/origin/main\n");

        var model = new NodeProjectDiscoveryAdapter().Discover(fixture.Root,
            measurer: new RejectingMeasurer(), measuredKinds: UnitCommandKinds.None);

        Assert.Equal("main", model.IntegrationBranch?.Value);
        Assert.Equal(FactConfidence.High, model.IntegrationBranch?.Confidence);
        Assert.Single(model.Commands);
        Assert.Empty(model.Measurements);
        Assert.Single(model.Units);
        Assert.Empty(model.TestSetups);
        Assert.Empty(model.Dependencies);
    }

    private static void AssertFact(ProjectModel model, string name, string value, string path, int line)
    {
        var fact = Assert.Single(model.EnvironmentNeeds, need => need.Name == name).Value;
        Assert.Equal(value, fact.Value);
        Assert.Equal(FactConfidence.High, fact.Confidence);
        Assert.Equal(path, fact.Source.Path);
        Assert.Equal(line, fact.Source.Line);
    }

    private sealed class RejectingMeasurer : IUnitCommandMeasurer
    {
        public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command) =>
            throw new InvalidOperationException("No command measurement was requested.");
    }

    private sealed class RecordingMeasurer : IUnitCommandMeasurer
    {
        internal List<(string Root, string UnitId, UnitCommandKinds Kind, string Command)> Calls { get; } = [];

        public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command)
        {
            Calls.Add((repositoryRoot, unitId, kind, command));
            return UnitCommandMeasurementResult.Succeeded(32);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("node-discovery-");
        internal string Root => _root.FullName;

        internal Fixture(string package = DefaultPackage, bool hasTsconfig = true, IReadOnlyList<string>? lockfiles = null)
        {
            Write("package.json", package);
            foreach (var file in lockfiles ?? ["package-lock.json"])
                Write(file, "{}");
            if (hasTsconfig)
                Write("tsconfig.json", "existence alone establishes type-check capability");
            Write("vitest.config.ts", "export default {};");
            Write("vite.config.ts", "export default {};");
        }

        internal void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        internal ProjectModel Discover() => new NodeProjectDiscoveryAdapter().Discover(Root);

        internal static OwnerAnswer[] Answers() =>
        [
            new("engine/timeouts/defaultMinutes", "5", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/buildServerShutdownMinutes", "3", "fixture", DateTimeOffset.UnixEpoch),
            new("engine/timeouts/discoveryMinutes", "4", "fixture", DateTimeOffset.UnixEpoch)
        ];

        public void Dispose() => _root.Delete(recursive: true);

        private const string DefaultPackage = """
            {
              "type": "module",
              "private": true,
              "scripts": {
                "test": "node --test && browser-performance && vitest run",
                "build": "tsc --noEmit && vite build"
              },
              "devDependencies": {
                "vitest": "4.1.10",
                "typescript": "^5.0.0"
              }
            }
            """;
    }
}
