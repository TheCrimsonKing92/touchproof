using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: synthetic inputs and immutable repository reads; no shared state or test hosts.
[LiveTreeStructural]
public sealed class AcceptanceOwnedCollectionLaneTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\"filter\": null,")]
    [InlineData("\"filter\": \"\",")]
    [InlineData("\"filter\": \" \\t \",")]
    public void Settings_FilterlessCollectionOwner_IsAccepted(string filterProperty)
    {
        var settings = ParseLane(filterProperty, "[\"Owned\"]");

        var lane = Assert.Single(settings.InfrastructureTestLanes);
        Assert.NotNull(lane.Filter);
        Assert.True(string.IsNullOrWhiteSpace(lane.Filter));
        Assert.Equal(["Owned"], lane.OwnedCollections);
    }

    [Fact]
    public void Settings_MissingFilterWithoutCollection_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => ParseLane("", "[]"));

        Assert.Equal("Acceptance manifest test lanes require non-empty names and filters.", exception.Message);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\" \\t \"")]
    [InlineData("null")]
    public void Settings_BlankFilterWithoutCollection_IsRejected(string filterJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ParseLane($"\"filter\": {filterJson},", "[]"));

        Assert.Equal("Acceptance manifest test lanes require non-empty names and filters.", exception.Message);
    }

    [Theory]
    [InlineData("[\"\"]")]
    [InlineData("[\" Owned \"]")]
    public void Settings_FilterlessOwnerWithInvalidCollection_IsRejected(string collectionsJson)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ParseLane("", collectionsJson));

        Assert.Equal("Acceptance manifest test lane 'Owner' has an invalid owned collection.", exception.Message);
    }

    [Fact]
    public void Settings_FilterlessDuplicateCollectionOwners_AreRejected()
    {
        const string manifest = """
            {"engine":{"infrastructureTestLanes":[
              {"name":"First","ownedCollections":["Owned"]},
              {"name":"Second","ownedCollections":["Owned"]}
            ]}}
            """;

        var exception = Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(manifest));

        Assert.Equal("Acceptance manifest collection 'Owned' has multiple owners.", exception.Message);
    }

    [Fact]
    public void Settings_NonblankOwnerWithoutInclusion_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ParseLane("\"filter\": \"FullyQualifiedName!~Outside\",", "[\"Owned\"]"));

        Assert.Equal("Acceptance manifest test lane 'Owner' must have an inclusion filter to own a collection.",
            exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Resolution_FilterlessOwner_SelectsOnlyCollectionAndExcludesRemainder(string filter)
    {
        AcceptanceTestLane[] lanes =
        [
            new("Owner", filter) { OwnedCollections = ["Owned"] },
            new("Named", "FullyQualifiedName~OutsideTests"),
            new("Remainder", "FullyQualifiedName!~OutsideTests")
        ];
        AcceptanceTestClassDescriptor[] classes =
        [
            new("Synthetic.FirstTests", "Owned"),
            new("Synthetic.SecondTests", "Owned"),
            new("Synthetic.OutsideTests", "Other"),
            new("Synthetic.UncollectedTests", null)
        ];

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes);

        Assert.Equal(["Synthetic.FirstTests", "Synthetic.SecondTests"], Selected(resolved[0], classes));
        Assert.Equal(["Synthetic.UncollectedTests"], Selected(resolved[2], classes));
        foreach (var item in classes)
            Assert.Single(AcceptanceLaneMembership.LanesIncluding(resolved, item.FullName));
        Assert.Empty(TestCollectionGuardRules.FindLaneViolations(lanes, classes));
        Assert.Empty(AcceptanceManifestLaneInvariants.FindViolations(lanes, classes));
    }

    [Fact]
    public void Resolution_MultipleOwnedCollections_SelectsDeduplicatedUnion()
    {
        AcceptanceTestLane[] lanes = [new("Owner", "") { OwnedCollections = ["First", "Second"] }];
        AcceptanceTestClassDescriptor[] classes =
        [
            new("Synthetic.SecondTests", "Second"),
            new("Synthetic.FirstTests", "First"),
            new("Synthetic.FirstTests", "First"),
            new("Synthetic.OutsideTests", "Other")
        ];

        var lane = Assert.Single(AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes));

        Assert.Equal(["Synthetic.FirstTests", "Synthetic.SecondTests"], Selected(lane, classes));
        var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
        Assert.Equal(2, arguments.Count(argument => argument == "--filter-class"));
    }

    [Fact]
    public void Resolution_EmptyOwnedCollection_SelectsNothingAndReportsEmptyLane()
    {
        AcceptanceTestLane[] lanes = [new("Owner", "") { OwnedCollections = ["Missing"] }];
        AcceptanceTestClassDescriptor[] classes =
        [
            new("Synthetic.OutsideTests", "Other"),
            new("Mcg.NoOwnedCollectionClasses", null)
        ];

        var lane = Assert.Single(AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes));

        Assert.False(string.IsNullOrWhiteSpace(lane.Filter));
        Assert.Empty(Selected(lane, classes));
        Assert.Equal("Lane 'Owner' selects no scanned class.",
            Assert.Single(AcceptanceManifestLaneInvariants.FindViolations(lanes, classes)));
    }

    [Fact]
    public void Matcher_UnresolvedFilterlessOwner_SelectsNothing()
    {
        var owner = new AcceptanceTestLane("Owner", "") { OwnedCollections = ["Owned"] };

        Assert.False(AcceptanceLaneMembership.CreateClassMatcher(owner)("Synthetic.AnyTests"));
        Assert.True(AcceptanceLaneMembership.CreateClassMatcher(new("Ad hoc", ""))("Synthetic.AnyTests"));
    }

    [Fact]
    public void Invariants_NonblankOwnerMatchingNothing_StillReportsEmptyLane()
    {
        AcceptanceTestLane[] lanes =
            [new("Owner", "FullyQualifiedName~MissingTests") { OwnedCollections = ["Owned"] }];
        AcceptanceTestClassDescriptor[] classes = [new("Synthetic.PresentTests", "Owned")];

        Assert.Equal("Lane 'Owner' selects no scanned class.",
            Assert.Single(AcceptanceManifestLaneInvariants.FindViolations(lanes, classes)));
        Assert.Contains(TestCollectionGuardRules.FindLaneViolations(lanes, classes),
            violation => violation.Rule == TestCollectionGuardRules.LaneMembershipRule);
    }

    [Fact]
    public void Guard_FilterlessOwnerOverlappingNamedLane_ReportsDuplicateSelection()
    {
        AcceptanceTestLane[] lanes =
        [
            new("Owner", "") { OwnedCollections = ["Owned"] },
            new("Named", "FullyQualifiedName~PresentTests")
        ];
        AcceptanceTestClassDescriptor[] classes = [new("Synthetic.PresentTests", "Owned")];

        var violation = Assert.Single(TestCollectionGuardRules.FindLaneViolations(lanes, classes));

        Assert.Equal(TestCollectionGuardRules.LanePartitionRule, violation.Rule);
        Assert.Equal("Synthetic.PresentTests", violation.ClassFullName);
    }

    [Fact]
    public void Manifest_FilterlessProcessLocalLane_HasExactExclusiveMembers()
    {
        const string laneName = "Process spawning process-local";
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "acceptance-manifest.json")))!;
        var laneNode = Assert.Single(manifest["engine"]!["infrastructureTestLanes"]!.AsArray(),
            lane => lane!["name"]!.GetValue<string>() == laneName)!.AsObject();
        laneNode.Remove("filter");
        var settings = AcceptanceGateEngineSettings.Parse(manifest.ToJsonString());
        var declaredLane = Assert.Single(settings.InfrastructureTestLanes, lane => lane.Name == laneName);
        Assert.True(string.IsNullOrWhiteSpace(declaredLane.Filter));
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        Assert.NotEmpty(classes);
        string[] expected =
        [
            typeof(ChildOutputDrainAdoptionSaturationTests).FullName!,
            typeof(PipeDrainThreadPoolSaturationTests).FullName!
        ];

        var resolved = AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, classes);

        Assert.Equal(expected.Order(StringComparer.Ordinal),
            Selected(Assert.Single(resolved, lane => lane.Name == laneName), classes));
        foreach (var name in expected)
            Assert.Equal(laneName, Assert.Single(AcceptanceLaneMembership.LanesIncluding(resolved, name)).Name);
        Assert.Empty(TestCollectionGuardRules.FindLaneViolations(settings.InfrastructureTestLanes, classes));
        Assert.Empty(AcceptanceManifestLaneInvariants.FindViolations(settings.InfrastructureTestLanes, classes));
        var expanded = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), Assert.IsType<AcceptanceBalancedLanePoolSettings>(settings.BalancedLanePool));
        foreach (var name in expected)
            Assert.Equal(laneName, Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, name)).Name);
    }

    private static AcceptanceGateEngineSettings ParseLane(string filterProperty, string collectionsJson) =>
        AcceptanceGateEngineSettings.Parse($$"""
            {
              "engine": {
                "infrastructureTestLanes": [
                  {"name":"Owner",{{filterProperty}}"ownedCollections":{{collectionsJson}} }
                ]
              }
            }
            """);

    private static string[] Selected(AcceptanceTestLane lane, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        var matches = AcceptanceLaneMembership.CreateClassMatcher(lane);
        return classes.Where(item => matches(item.FullName)).Select(item => item.FullName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
}
