using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable manifest, fixture and source reads; no history, git or test hosts.
public sealed class AcceptanceExecutedLaneCoverageTests
{
    [Fact]
    public void BeforeAndAfterLaneSets_EveryRetainedScannedClassExecutesExactlyOnce()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var current = AcceptanceGateEngineSettings.Load(root);
        // Snapshot of pool settings and all 30 lanes at 44e9b1c7e, before this cleanup.
        // Retire this comparison when a later slice intentionally changes execution routing.
        var baseline = LoadBaseline(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);

        var before = Expand(baseline, classes);
        var after = Expand(current, classes);
        Assert.Empty(CoverageErrors(before.Lanes, classes));
        Assert.Empty(CoverageErrors(after.Lanes, classes));
        var beforePool = before.Layout!.Lanes.SelectMany(lane => lane.Members).ToHashSet(StringComparer.Ordinal);
        var afterPool = after.Layout!.Lanes.SelectMany(lane => lane.Members).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(beforePool);
        Assert.Equal(beforePool.Order(StringComparer.Ordinal), afterPool.Order(StringComparer.Ordinal));
        foreach (var item in classes.Where(item => !beforePool.Contains(item.FullName)))
            Assert.Equal(Assert.Single(AcceptanceLaneMembership.LanesIncluding(before.Lanes, item.FullName)).Name,
                Assert.Single(AcceptanceLaneMembership.LanesIncluding(after.Lanes, item.FullName)).Name);

        // Raw local partitions and the Developer preflight still need exactly one owner too.
        Assert.Empty(TestCollectionGuardRules.FindLaneViolations(current.InfrastructureTestLanes, classes));
    }

    [Theory]
    [InlineData(false, "Synthetic.RetainedTests: []")]
    [InlineData(true, "Synthetic.RetainedTests: [first, second]")]
    public void Coverage_MissingOrDuplicateSelection_ReportsClassAndOwners(bool duplicate, string expected)
    {
        AcceptanceTestClassDescriptor[] classes = [new("Synthetic.RetainedTests", null)];
        AcceptanceTestLane[] lanes = duplicate
            ? [new("first", "FullyQualifiedName~RetainedTests"), new("second", "FullyQualifiedName~RetainedTests")]
            : [];

        Assert.Equal(expected, Assert.Single(CoverageErrors(lanes, classes)));
    }

    private static AcceptanceBalancedLaneExpansion Expand(AcceptanceGateEngineSettings settings,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes) =>
        AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool));

    private static string[] CoverageErrors(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes) =>
        classes.Select(item => (item.FullName, Owners: AcceptanceLaneMembership.LanesIncluding(lanes, item.FullName)))
            .Where(item => item.Owners.Count != 1)
            .Select(item => $"{item.FullName}: [{string.Join(", ", item.Owners.Select(lane => lane.Name))}]").ToArray();

    private static AcceptanceGateEngineSettings LoadBaseline(string root)
    {
        var path = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures",
            "AcceptanceLaneBaseline", "lanes-before-e855c460.json");
        try
        {
            return AcceptanceGateEngineSettings.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"Cannot load pre-change lane fixture '{path}'.", exception);
        }
    }
}
