using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its manifest, scanner sources and duration store.
public sealed class AcceptanceIsolationConstraintIdentityTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("constraint-plan-identity-");
    private const string SourceRoot = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests";

    public AcceptanceIsolationConstraintIdentityTests()
    {
        Directory.CreateDirectory(Path.Combine(_root.FullName, "config"));
        Directory.CreateDirectory(Path.Combine(_root.FullName, SourceRoot));
        File.WriteAllText(Path.Combine(_root.FullName, "config", "acceptance-manifest.json"), """
            {
              "version": 1,
              "engine": {
                "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                "balancedLanePool": {
                  "historyWindow": 1,
                  "isolationConstraints": [{"name":"ProcessSpawning","laneCount":2}]
                }
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj"
              }]
            }
            """);
    }

    [Fact]
    public void DurationHistoryChangesConstraintPlacementAndPreservesIdentity()
    {
        WriteClasses(tagFirst: true);
        Record("first", 100, 1, 80);
        var original = Identity();
        var before = ConstraintExecutionFilters();

        Record("second", 1, 100, 80);

        Assert.Equal(2, before.Length);
        Assert.NotEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(ConstraintExecutionFilters()));
        Assert.Equal(original, Identity());
        var settings = AcceptanceGateEngineSettings.Load(_root.FullName);
        var lanes = AcceptanceInfrastructureLaneResolver.ResolveIdentity(settings, _root.FullName).Lanes;
        var canonical = Assert.Single(lanes, lane => lane.Name.StartsWith("ProcessSpawning ", StringComparison.Ordinal));
        Assert.Equal("FullyQualifiedName=Tests.A|FullyQualifiedName=Tests.B|FullyQualifiedName=Tests.C", canonical.Filter);
    }

    [Fact]
    public void GainingAndLosingIsolationTraitChangesIdentity()
    {
        WriteClasses(tagFirst: false);
        var untagged = Identity();

        WriteClasses(tagFirst: true);
        var tagged = Identity();
        Assert.NotEqual(untagged, tagged);

        WriteClasses(tagFirst: false);
        Assert.Equal(untagged, Identity());
    }

    private string Identity() => GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(_root.FullName);

    private string[] ConstraintExecutionFilters() =>
        AcceptanceInfrastructureLaneResolver.ResolveExecution(AcceptanceGateEngineSettings.Load(_root.FullName), _root.FullName)
            .Lanes.Where(lane => lane.Name.StartsWith("ProcessSpawning ", StringComparison.Ordinal))
            .Select(lane => lane.Filter).ToArray();

    private void Record(string attempt, double a, double b, double c) =>
        Assert.Null(AcceptanceClassDurationHistoryStore.RecordAttempt(_root.FullName, attempt,
            [new(attempt, "ProcessSpawning 1", "Tests.A", a), new(attempt, "ProcessSpawning 2", "Tests.B", b),
                new(attempt, "ProcessSpawning 2", "Tests.C", c)], 1, completed: true, passed: true));

    private void WriteClasses(bool tagFirst)
    {
        const string trait = """[Xunit.Trait("Isolation", "ProcessSpawning")]""";
        File.WriteAllText(Path.Combine(_root.FullName, SourceRoot, "Fixture.cs"), $$"""
            namespace Tests;
            {{(tagFirst ? trait : "")}}
            public class A { [Xunit.Fact] public void Runs() {} }
            {{trait}}
            public class B { [Xunit.Fact] public void Runs() {} }
            {{trait}}
            public class C { [Xunit.Fact] public void Runs() {} }
            """);
    }

    public void Dispose() => _root.Delete(recursive: true);
}
