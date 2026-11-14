using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable repository reads and synthetic lane inputs; no shared state.
[LiveTreeStructural]
public sealed class ProcessSpawningSplitLaneCollectionMembershipTests
{
    [Fact]
    public void Manifest_CollectionOwners_SelectExactMembersAndEmptyOwnerSelectsNothing()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, classes);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool));
        (string Name, string Collection)[] owners =
        [
            ("Process spawning host-bound", "ProcessSpawningHostBound"),
            ("Process spawning process-local", "ProcessSpawningProcessLocal")
        ];
        foreach (var (name, collection) in owners)
        {
            var declared = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == name);
            Assert.True(AcceptanceLaneMembership.IsFilterlessCollectionOwner(declared));
            Assert.Equal([collection], declared.OwnedCollections);
            var expected = classes.Where(item => item.Collection == collection).Select(item => item.FullName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Selected(Assert.Single(resolved, lane => lane.Name == name), classes));
            foreach (var member in expected)
            {
                Assert.Equal(name, Assert.Single(AcceptanceLaneMembership.LanesIncluding(resolved, member)).Name);
                Assert.Equal(name, Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, member)).Name);
                Assert.DoesNotContain(resolved, lane => lane.Name != name && lane.Name != "Remainder" &&
                    AcceptanceLaneMembership.CreateClassMatcher(lane)(member));
            }
        }

        const string absentCollection = "SyntheticMissingSplitLaneCollection";
        Assert.DoesNotContain(classes, item => item.Collection == absentCollection);
        AcceptanceTestLane[] synthetic =
        [
            new("Present owner", "") { OwnedCollections = [owners[0].Collection] },
            new("Empty owner", "") { OwnedCollections = [absentCollection] }
        ];
        var control = AcceptanceLaneMembership.ResolveOwnedCollections(synthetic, classes);
        var emptyOwner = Assert.Single(control, lane => lane.Name == "Empty owner");
        Assert.False(string.IsNullOrWhiteSpace(emptyOwner.Filter));
        Assert.Empty(Selected(emptyOwner, classes));
        Assert.All(classes, item => Assert.DoesNotContain(
            AcceptanceLaneMembership.LanesIncluding(control, item.FullName), lane => lane.Name == emptyOwner.Name));
        var present = classes.Where(item => item.Collection == owners[0].Collection).Select(item => item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(present);
        Assert.Equal(present, Selected(Assert.Single(control, lane => lane.Name == "Present owner"), classes));
    }

    private static string[] Selected(AcceptanceTestLane lane, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var matches = AcceptanceLaneMembership.CreateClassMatcher(lane);
        return classes.Where(item => matches(item.FullName)).Select(item => item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
