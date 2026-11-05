using Mcg.AgentOrchestrator.Infrastructure;
using TestDescriptor = MtpTestRunnerScriptTests.ManagedTestDescriptor;

// Parallel-safe: synthetic catalogs and immutable manifest/source/attribute reads only.
public sealed class AcceptanceBalancedLanePoolConstraintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedLanes_ScannedOrMissedHostTest_KeepPoolConstraints(bool scanned)
    {
        TestDescriptor[] catalog = [Test("Tests.A"), Test("Tests.B"), Test("Tests.Missed"),
            Test("Tests.Host", host: true)];
        AcceptanceTestClassDescriptor[] classes = [new("Tests.A", null), new("Tests.B", null)];
        if (scanned) classes = [.. classes, new("Tests.Host", null)];
        var expansion = AcceptanceBalancedLaneExpander.Expand([], classes, new Dictionary<string, double>(),
            new() { LaneCount = 2, TraitExclusions = ["Category!=HostIntegration"],
                ExclusiveResourceKeys = ["machine:custom-resource"] });

        Assert.Equal(2, expansion.Layout!.Lanes.Count);
        Assert.Equal(3, expansion.Lanes.Count);
        var selected = new List<string>();
        foreach (var lane in expansion.Lanes)
        {
            Assert.Equal(["machine:custom-resource"], lane.ExclusiveResourceKeys);
            var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
            Assert.True(AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(arguments, "Category=HostIntegration"));
            var matches = MtpFilterArgumentSemantics.Select(arguments, catalog);
            Assert.NotEmpty(matches);
            Assert.DoesNotContain(matches, test => test.TypeName == "Tests.Host");
            selected.AddRange(matches.Select(test => test.TypeName));
        }
        Assert.Equal(["Tests.A", "Tests.B", "Tests.Missed"], selected.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ShippedManifest_ExpandedLayout_ExcludesRealHostIntegrationMethods()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var settings = AcceptanceGateEngineSettings.Load(root);
        var scanned = AcceptanceTestClassSourceScanner.Scan(root);
        var hostMethods = new[] { typeof(SshRemoteLaneExecutorRealTransportTests), typeof(GoalWorktreeTestsSeedIsolation) }
            .SelectMany(type => type.GetMethods().Select(method => new TestDescriptor(method.Name, method.Name,
                type.FullName!, method.Name, method.GetCustomAttributes(typeof(Xunit.TraitAttribute), inherit: true)
                    .Cast<Xunit.TraitAttribute>().Select(trait => new KeyValuePair<string, string>(trait.Name, trait.Value)).ToArray())))
            .Where(test => test.Traits.Any(trait => trait.Key == "Category" && trait.Value == "HostIntegration"))
            .ToArray();
        Assert.Equal(2, hostMethods.Length);

        var expansion = AcceptanceBalancedLaneExpander.Expand(settings.InfrastructureTestLanes, scanned,
            new Dictionary<string, double>(), settings.BalancedLanePool!);
        var selectedWithoutTraitExclusions = new List<TestDescriptor>();
        foreach (var lane in expansion.Lanes)
        {
            var arguments = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
            Assert.Empty(MtpFilterArgumentSemantics.Select(arguments, hostMethods));
            var withoutTraits = arguments.Chunk(2).Where(pair => pair[0] != "--filter-not-trait")
                .SelectMany(pair => pair).ToArray();
            selectedWithoutTraitExclusions.AddRange(MtpFilterArgumentSemantics.Select(withoutTraits, hostMethods));
        }
        // Scanner-missed custom Facts can match both a constrained lane and the residual.
        // Removing only the trait guards must expose both methods, regardless of that overlap.
        Assert.Equal(hostMethods.OrderBy(test => test.TypeName, StringComparer.Ordinal),
            selectedWithoutTraitExclusions.DistinctBy(test => (test.TypeName, test.MethodName))
                .OrderBy(test => test.TypeName, StringComparer.Ordinal));
        var generated = expansion.Lanes.Where(lane => expansion.Layout!.Lanes.Any(item => item.Name == lane.Name) ||
            lane.Name == settings.BalancedLanePool!.ResidualLaneName).ToArray();
        Assert.Equal(expansion.Layout!.Lanes.Count + 1, generated.Length);
        Assert.Empty(settings.BalancedLanePool!.ExclusiveResourceKeys);
        Assert.All(generated, lane => Assert.Empty(lane.ExclusiveResourceKeys));
    }

    [Theory]
    [InlineData("")]
    [InlineData("FullyQualifiedName=Tests.Host")]
    [InlineData("Category!=HostIntegration&Category!=Other")]
    public void TraitExclusion_NotOneTrait_RejectsSettings(string exclusion) =>
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { TraitExclusions = [exclusion] }.Validate([]));

    [Theory]
    [InlineData("")]
    [InlineData("MACHINE:resource")]
    public void ResourceKeys_EmptyOrDuplicate_RejectsSettings(string key) =>
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { ExclusiveResourceKeys = ["machine:resource", key] }.Validate([]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void PoolEligibility_InvalidNames_RejectsSettings(string? name)
    {
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = [name!] }.Validate([]));
    }

    [Fact]
    public void PoolEligibility_NullOrDuplicateNames_RejectsSettings()
    {
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = null! }.Validate([]));
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = ["Local", "local"] }.Validate([]));
    }

    [Fact]
    public void PoolEligibility_UnknownName_RejectsSettings()
    {
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = ["Missing"] }.Validate([new("Local", "FullyQualifiedName~Local")]));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("collection")]
    [InlineData("build")]
    [InlineData("trait")]
    public void PoolEligibility_UnpreservedIsolation_RejectsSettings(string constraint)
    {
        var lane = new AcceptanceTestLane("Local", "FullyQualifiedName~Local");
        lane = constraint switch
        {
            "resource" => lane with { ExclusiveResourceKeys = ["machine:resource"] },
            "collection" => lane with { OwnedCollections = ["owned"] },
            "build" => lane with { RequiresBuildSystemChange = true },
            "trait" => lane with { Filter = "FullyQualifiedName~Local&Category!=Other" },
            _ => throw new InvalidOperationException("Unknown test constraint.")
        };
        Assert.Throws<InvalidDataException>(() => new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = ["Local"], TraitExclusions = ["Category!=HostIntegration"] }.Validate([lane]));
    }

    [Fact]
    public void PoolEligibility_MatchingTraits_MoveOnlyTheNamedLane()
    {
        AcceptanceTestLane[] lanes = [
            new("Local", "FullyQualifiedName~Local&Category!=HostIntegration"),
            new("Protected", "FullyQualifiedName~Protected&Category!=HostIntegration")];
        AcceptanceTestClassDescriptor[] classes = [new("Tests.Local", null), new("Tests.Protected", null)];
        var settings = new AcceptanceBalancedLanePoolSettings
            { PoolEligibleLaneNames = ["local"], TraitExclusions = ["Category!=HostIntegration"] };

        var expansion = AcceptanceBalancedLaneExpander.Expand(lanes, classes, new Dictionary<string, double>(), settings);

        Assert.All(lanes, lane => Assert.True(AcceptanceBalancedLaneExpander.IsConstrained(lane)));
        Assert.DoesNotContain(expansion.Lanes, lane => lane.Name == "Local");
        Assert.Equal(lanes[1], Assert.Single(expansion.Lanes, lane => lane.Name == "Protected"));
        Assert.Equal(["Tests.Local"], Assert.Single(expansion.Layout!.Lanes).Members);
        var poolLane = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expansion.Lanes, "Tests.Local"));
        Assert.StartsWith("Balanced ", poolLane.Name);
        Assert.True(AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(poolLane.Filter).ToArray(), "Category=HostIntegration"));
    }

    private static TestDescriptor Test(string name, bool host = false) => new(name, name, name, "Runs",
        host ? [new("Category", "HostIntegration")] : []);
}
