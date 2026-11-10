using Mcg.AgentOrchestrator.Core;

// Parallel-safe: one immutable candidate snapshot; no process or mutable shared state.
[LiveTreeStructural]
public sealed class ProductionMemberReflectionGuardTests
{
    [Fact]
    public void NamedProductionLookupsRequireExactReasonedExemptions()
    {
        var root = VerifiedRepositoryRoot.Find();
        var snapshot = RepositorySourceSnapshotCache.Get(root);
        // Explicit dependencies are consumed by the deterministic pre-Tester reference reader.
        var tests = snapshot.EnumerateFiles(Path.Combine(root, "tests"));
        var production = snapshot.EnumerateFiles(Path.Combine(root, "src"));
        Assert.NotEmpty(production);
        var exemptions = ProductionMemberReflectionScanner.ReadExemptions(File.ReadAllText(
            Path.Combine(root, "config", "production-member-reflection-exemptions.json")));
        var result = ProductionMemberReflectionScanner.ScanRepository(snapshot, tests, exemptions);
        Assert.NotEmpty(result.Lookups);
        Assert.True(result.Diagnostics.Count == 0, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
