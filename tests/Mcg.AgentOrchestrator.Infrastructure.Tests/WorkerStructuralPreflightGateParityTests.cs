using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each real host request uses a fixture-owned repository, stores and fake compiler.
public sealed class WorkerStructuralPreflightGateParityTests
{
    [Fact]
    public async Task RatchetViolation_ReportsExactGateFindings()
    {
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root => WorkerBuildRequestFixture.SeedSizePolicy(root));
        // This is the ratchet evaluator invoked by GoalAcceptanceVerifier for the same tree.
        var gate = SourceSizeRatchetPreflight.Evaluate(fixture.RepositoryRoot);
        Assert.True(gate.EvaluationComplete);
        Assert.True(gate.HasBlockingViolation);

        var invocation = await fixture.RunHelper();

        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, invocation.StructuralCheck!.Status);
        Assert.Equal(gate.Message, invocation.StructuralCheck.Diagnostic);
        Assert.Equal(gate.Message, Assert.Single(fixture.Responses).StructuralDiagnostic);
    }

    [Fact]
    public async Task ManifestCoverageViolation_ReportsExactGateFindings()
    {
        const string relative = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CoverageOrphanFixture.cs";
        using var fixture = new WorkerBuildRequestFixture(compilerMode: WorkerBuildRequestFixture.CompilerMode.Fake,
            seedRepository: root =>
            {
                Write(root, "config/acceptance-manifest.json", """
                    { "version": 1, "engine": {
                      "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],
                      "infrastructureTestLanes": [
                        { "name": "Dotnet build slots", "filter": "FullyQualifiedName~ConductorBatchLoopTests", "ownedCollections": ["DotnetBuildSlots"] },
                        { "name": "Assigned", "filter": "FullyQualifiedName~AssignedFixture" }
                      ] } }
                    """);
                Write(root, TestCollectionGuardRules.ExemptionsRelativePath, "[]");
                Write(root, relative, "namespace Fixture; public class CoverageOrphanFixture { [Xunit.Fact] public void Case() { } }");
            });
        var lanes = AcceptanceGateEngineSettings.Load(fixture.RepositoryRoot).InfrastructureTestLanes;
        var descriptors = AcceptanceTestClassSourceScanner.Scan(fixture.RepositoryRoot);
        Assert.Equal("Fixture.CoverageOrphanFixture", Assert.Single(descriptors).FullName);
        // These are the gate suite's shared structural rules, independent of the Developer adapter.
        var gateFindings = TestCollectionGuardRules.FindLaneViolations(lanes, descriptors)
            .Concat(TestCollectionGuardRules.FindUnisolatedLaneClasses(lanes, descriptors,
                TestCollectionGuardRules.DotnetBuildSlotsLaneName, TestCollectionGuardRules.DotnetBuildSlotsCollection,
                TestCollectionGuardRules.LoadIsolatedCollectionExemptions(fixture.RepositoryRoot).Keys)).ToArray();
        var gate = Assert.Single(gateFindings);
        Assert.Equal(TestCollectionGuardRules.LanePartitionRule, gate.Rule);
        var expected = $"test collection guard violations: {relative}: {gate.ClassFullName} [{gate.Rule}] {gate.RepairHint}";

        var invocation = await fixture.RunHelper();

        Assert.Equal(WorkerBuildOutcome.Success, invocation.Outcome);
        Assert.Equal(WorkerBuildStructuralStatus.Violation, invocation.StructuralCheck!.Status);
        Assert.Equal(expected, invocation.StructuralCheck.Diagnostic);
        Assert.Equal(expected, Assert.Single(fixture.Responses).StructuralDiagnostic);
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
