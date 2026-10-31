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
        // Custom Fact attributes can reach the residual instead of the scanner's class list.
        // Both methods must be selected exactly once when only the trait guard is removed.
        Assert.Equal(hostMethods.OrderBy(test => test.TypeName, StringComparer.Ordinal),
            selectedWithoutTraitExclusions.OrderBy(test => test.TypeName, StringComparer.Ordinal));
        var generated = expansion.Lanes.Where(lane => expansion.Layout!.Lanes.Any(item => item.Name == lane.Name) ||
            lane.Name == settings.BalancedLanePool!.ResidualLaneName).ToArray();
        Assert.Equal(expansion.Layout!.Lanes.Count + 1, generated.Length);
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

    private static TestDescriptor Test(string name, bool host = false) => new(name, name, name, "Runs",
        host ? [new("Category", "HostIntegration")] : []);
}
