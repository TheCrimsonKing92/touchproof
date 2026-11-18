using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable metadata and source reads, with an isolated history fixture.
[TestAssemblyMembership]
public sealed class IsolatedProcessSpawningRemainderChainTests
{
    private const string Constraint = "ProcessSpawning";
    private const string OriginalCollection = "IsolatedProcessSpawning";
    private const string SplitCollection = "IsolatedProcessSpawningRemainderB";
    private const string DeferredContextCollection = "IsolatedProcessSpawningRemainderC";
    private const double BudgetSeconds = 240;

    [Fact]
    public void SplitCollectionHasParallelSharedFixtureAndExactMembers()
    {
        AssertParallelSharedFixture(typeof(IsolatedProcessSpawningRemainderBCollection), SplitCollection);
        AssertParallelSharedFixture(typeof(IsolatedProcessSpawningRemainderCCollection), DeferredContextCollection);

        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var tagged = AcceptanceTestClassSourceScanner.Scan(root).Where(item => item.Constraint == Constraint).ToArray();
        var types = typeof(IsolatedProcessSpawningRemainderChainTests).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .Any(method => method.IsDefined(typeof(Xunit.FactAttribute), inherit: true)))
            .ToArray();
        var reflectedTags = types.Where(type => type.GetCustomAttributes<Xunit.TraitAttribute>(inherit: true)
            .Any(trait => trait.Name == "Isolation" && trait.Value == Constraint)).Select(type => type.FullName!);
        Assert.Equal(reflectedTags.Order(StringComparer.Ordinal), tagged.Select(item => item.FullName));

        foreach (var collection in new[] { SplitCollection, DeferredContextCollection })
        {
            var members = types.Where(type => type.GetCustomAttribute<Xunit.CollectionAttribute>(inherit: true)?.Name == collection)
                .Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(members);
            Assert.Equal(members, tagged.Where(item => item.Collection == collection).Select(item => item.FullName));
        }
    }

    [Fact]
    public void ShippedManifestSelectsEachChainClassExactlyOnce()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var manifest = AcceptanceGateEngineSettings.Load(root);
        var pool = Assert.IsType<AcceptanceBalancedLanePoolSettings>(manifest.BalancedLanePool);
        var constraint = Assert.Single(pool.IsolationConstraints!, item => item.Name == Constraint);
        var scanned = AcceptanceTestClassSourceScanner.Scan(root);
        var expanded = AcceptanceBalancedLaneExpander.Expand(manifest.InfrastructureTestLanes, scanned,
            new Dictionary<string, double>(), pool);
        var chain = scanned.Where(item => item.Collection is OriginalCollection or SplitCollection or DeferredContextCollection).ToArray();
        Assert.NotEmpty(chain);
        Assert.All(chain, item => Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName)));
        var tagged = scanned.Where(item => item.Constraint == Constraint).ToArray();
        Assert.NotEmpty(tagged);
        foreach (var item in tagged)
        {
            Assert.All(manifest.InfrastructureTestLanes, lane =>
                Assert.Empty(AcceptanceLaneMembership.MatchingIncludePatterns(lane, item.FullName)));
            var owner = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName));
            Assert.StartsWith(Constraint + " ", owner.Name);
            Assert.Equal(constraint.ExclusiveResourceKeys, owner.ExclusiveResourceKeys);
        }
    }

    [Fact]
    public void MeasuredPlacementFitsEachLaneBudgetAndRejectsSingleLaneControl()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var manifest = AcceptanceGateEngineSettings.Load(root);
        var pool = Assert.IsType<AcceptanceBalancedLanePoolSettings>(manifest.BalancedLanePool);
        var scanned = AcceptanceTestClassSourceScanner.Scan(root);
        var temporaryRoot = Directory.CreateTempSubdirectory("constraint-class-history-");
        try
        {
            var fixture = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures",
                "AcceptanceLaneBaseline", "process-spawning-constraint-class-history.json");
            var historyPath = AcceptanceClassDurationHistoryStore.ResolveStorePath(temporaryRoot.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.Copy(fixture, historyPath);
            var inputs = AcceptanceClassDurationHistoryStore.ReadModelInputs(temporaryRoot.FullName, pool.HistoryWindow, out var failure);
            Assert.Null(failure);
            Assert.NotEmpty(inputs);
            Assert.All(inputs.Values, input => Assert.Null(input.SlotCount));
            var medians = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Seconds, StringComparer.Ordinal);
            var expanded = AcceptanceBalancedLaneExpander.Expand(manifest.InfrastructureTestLanes, scanned, medians, pool, inputs);
            var isolated = expanded.Lanes.Where(lane => lane.Name.StartsWith(Constraint + " ", StringComparison.Ordinal)).ToArray();

            // The fixture has one-slot measurements, so predicted wall equals predicted serial seconds.
            Assert.True(isolated.Length > 1);
            Assert.All(isolated, lane => Assert.InRange(lane.EstimatedSerialSeconds, double.Epsilon, BudgetSeconds));

            var singleLane = AcceptanceBalancedLaneExpander.Expand([], scanned.Where(item => item.Constraint == Constraint).ToArray(),
                medians, new AcceptanceBalancedLanePoolSettings
                {
                    HistoryWindow = pool.HistoryWindow,
                    DefaultClassSecondsWhenNoHistory = pool.DefaultClassSecondsWhenNoHistory,
                    IsolationConstraints = [new() { Name = Constraint, LaneCount = 1 }]
                }, inputs);
            var merged = Assert.Single(singleLane.Lanes, lane => lane.Name == Constraint + " 1");
            Assert.True(merged.EstimatedSerialSeconds > BudgetSeconds);
        }
        finally
        {
            temporaryRoot.Delete(recursive: true);
        }
    }

    private static void AssertParallelSharedFixture(Type definitionType, string collection)
    {
        var definition = definitionType.GetCustomAttribute<Xunit.CollectionDefinitionAttribute>();
        Assert.NotNull(definition);
        Assert.Equal(collection, definition.Name);
        Assert.True(definitionType.IsPublic && definitionType.IsSealed);
        Assert.False(definition.DisableParallelization);
        Assert.Null(definitionType.GetCustomAttribute<ProcessLocalTestCollectionAttribute>());
        Assert.True(typeof(Xunit.ICollectionFixture<IsolatedDotnetRootFixture>).IsAssignableFrom(definitionType));
        Assert.Contains(collection, LaneIsolatedRootScanner.IsolatedCollections(definitionType.Assembly));
    }
}
