using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable manifest/source reads and synthetic inputs.
public sealed class AcceptanceBalancedLanePoolMembershipTests
{
    [Fact]
    public void ConstraintKinds_KeepTheirFilters_AndEveryClassHasOneLane()
    {
        AcceptanceTestLane[] lanes = [
            new("key", "FullyQualifiedName~Key") { ExclusiveResourceKeys = ["machine:resource"] },
            new("collection", "FullyQualifiedName~Owned") { OwnedCollections = ["owned"] },
            new("build", "FullyQualifiedName~Build") { RequiresBuildSystemChange = true },
            new("local", "FullyQualifiedName~Local&Category!=HostIntegration"),
            new("old catch all", "FullyQualifiedName!~Key&FullyQualifiedName!~Owned&FullyQualifiedName!~Build&FullyQualifiedName!~Local")];
        AcceptanceTestClassDescriptor[] classes = [new("Tests.Key", null), new("Tests.Owned", "owned"),
            new("Tests.Build", null), new("Tests.Local", null), new("Tests.New", null)];
        var expanded = AcceptanceBalancedLaneExpander.Expand(lanes, classes, new Dictionary<string, double>(), new());
        Assert.Equal(lanes.Take(4), expanded.Lanes.Take(4));
        foreach (var item in classes)
            Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName));
        Assert.Equal(["Tests.New"], Assert.Single(expanded.Layout!.Lanes).Members);
        Assert.True(Assert.Single(expanded.Lanes, lane => lane.Name == "Infrastructure residual").AllowsEmptySelection);
    }

    [Fact]
    public void ShippedManifest_EveryScannedClass_HasExactlyOneExpandedLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), settings.BalancedLanePool!);
        foreach (var item in classes)
        {
            var matches = AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, item.FullName);
            Assert.True(matches.Count == 1, item.FullName + ": " + string.Join(',', matches.Select(lane => lane.Name)));
        }
        Assert.NotEmpty(expanded.Layout!.Lanes.SelectMany(lane => lane.Members));
        var constrained = settings.InfrastructureTestLanes.Where(AcceptanceBalancedLaneExpander.IsConstrained).ToArray();
        Assert.All(expanded.Layout.Lanes.SelectMany(lane => lane.Members),
            name => Assert.Empty(AcceptanceLaneMembership.LanesIncluding(constrained, name)));
    }
}
