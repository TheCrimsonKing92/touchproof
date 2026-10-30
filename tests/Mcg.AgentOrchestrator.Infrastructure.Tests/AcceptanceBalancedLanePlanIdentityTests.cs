using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its manifest, scanner sources and duration store.
public sealed class AcceptanceBalancedLanePlanIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "balanced-plan-identity-" + Guid.NewGuid().ToString("N"));
    private const string SourceRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";
    private string ManifestPath => Path.Combine(_root, "config", "acceptance-manifest.json");

    public AcceptanceBalancedLanePlanIdentityTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        Directory.CreateDirectory(Path.Combine(_root, SourceRoot));
        File.WriteAllText(ManifestPath, """
            {
              "version": 1,
              "engine": {
                "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                "balancedLanePool": { "laneCount": 2, "historyWindow": 2 },
                "infrastructureTestLanes": [{
                  "name": "Constrained", "filter": "FullyQualifiedName=Tests.Key",
                  "exclusiveResourceKeys": ["machine:test-resource"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
        WriteClasses("A", "B", "C", "Key");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedHistory_ChangesExecutionLayout_ButPreservesPlanIdentity(bool scoped)
    {
        string[]? changedFiles = scoped ? [SourceRoot + "/Fixture.cs"] : null;
        Assert.Equal(["Tests.A", "Tests.B", "Tests.C", "Tests.Key"],
            AcceptanceTestClassSourceScanner.Scan(_root).Select(item => item.FullName));
        var original = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root, changedFiles);
        var before = ExecutionSignature(changedFiles);

        Record("first", 100, 1, 80);

        var after = ExecutionSignature(changedFiles);
        Assert.NotEqual(before, after);
        var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(_root, changedFiles);
        var first = Assert.Single(checks, check => check.Name == "infrastructure tests: Balanced 1");
        Assert.Equal("FullyQualifiedName=Tests.A", first.Arguments.Last());
        Assert.Equal(100d, first.EstimatedSerialSeconds);
        var second = Assert.Single(checks, check => check.Name == "infrastructure tests: Balanced 2");
        Assert.Equal("FullyQualifiedName=Tests.C|FullyQualifiedName=Tests.B", second.Arguments.Last());
        Assert.Equal(81d, second.EstimatedSerialSeconds);
        Assert.Equal(original, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root, changedFiles));

        Record("second", 2, 300, 1);

        Assert.NotEqual(after, ExecutionSignature(changedFiles));
        Assert.Equal(original, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root, changedFiles));
    }

    [Theory]
    [InlineData("laneCount", "3")]
    [InlineData("historyWindow", "3")]
    [InlineData("defaultClassSecondsWhenNoHistory", "2")]
    [InlineData("laneNamePrefix", "\"Pool\"")]
    [InlineData("residualLaneName", "\"Residual\"")]
    public void PoolContractChange_ChangesPlanIdentity(string setting, string value)
    {
        var before = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root);
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!;
        manifest["engine"]!["balancedLanePool"]![setting] = JsonNode.Parse(value);
        File.WriteAllText(ManifestPath, manifest.ToJsonString());

        Assert.NotEqual(before, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root));
    }

    [Fact]
    public void ScannedClassAdded_ChangesPlanIdentity()
    {
        var before = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root);
        WriteClasses("A", "B", "C", "Key", "New");

        Assert.Contains(AcceptanceTestClassSourceScanner.Scan(_root), item => item.FullName == "Tests.New");
        Assert.NotEqual(before, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root));
    }

    [Theory]
    [InlineData("filter", "\"FullyQualifiedName=Tests.Key|FullyQualifiedName=Tests.A\"")]
    [InlineData("exclusiveResourceKeys", "[\"machine:other-resource\"]")]
    public void ConstrainedLaneChange_ChangesPlanIdentity(string setting, string value)
    {
        var before = GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root);
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!;
        manifest["engine"]!["infrastructureTestLanes"]![0]![setting] = JsonNode.Parse(value);
        File.WriteAllText(ManifestPath, manifest.ToJsonString());

        Assert.NotEqual(before, GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root));
    }

    private string ExecutionSignature(string[]? changedFiles) => JsonSerializer.Serialize(
        GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(_root, changedFiles)
            .Select(check => new { check.Name, check.Arguments, check.EstimatedSerialSeconds }));

    private void Record(string attempt, double a, double b, double c) =>
        Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(_root, attempt,
            [new(attempt, "Balanced 1", "Tests.A", a), new(attempt, "Balanced 2", "Tests.B", b),
                new(attempt, "Balanced 2", "Tests.C", c)], 2, completed: true, passed: true));

    private void WriteClasses(params string[] names) => File.WriteAllText(Path.Combine(_root, SourceRoot, "Fixture.cs"),
        "namespace Tests; " + string.Join('\n', names.Select(name =>
            "public class " + name + " { [Xunit.Fact] public void Runs() {} }")));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
