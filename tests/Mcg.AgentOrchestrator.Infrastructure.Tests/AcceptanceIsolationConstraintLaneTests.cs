using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: synthetic inputs and immutable repository reads.
public sealed class AcceptanceIsolationConstraintLaneTests
{
    private const string Constraint = "ProcessSpawning";
    private const string Resource = "machine:isolated-test-resource";

    [Fact]
    public void NewlyTaggedClassHasExactlyOneConstraintLane()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var manifest = AcceptanceGateEngineSettings.Load(root);
        var pool = Assert.IsType<AcceptanceBalancedLanePoolSettings>(manifest.BalancedLanePool);
        var configured = Assert.Single(pool.IsolationConstraints!, item => item.Name == Constraint);
        const string name = "Synthetic.NewlyTaggedIsolationTests";
        var addition = Assert.Single(AcceptanceTestClassSourceScanner.ScanSourceTexts([
            """
            namespace Synthetic;
            [Xunit.Trait("Isolation", "ProcessSpawning")]
            public class NewlyTaggedIsolationTests { [Xunit.Fact] public void Runs() {} }
            """
        ]));
        var classes = AcceptanceTestClassSourceScanner.Scan(root).Append(addition).ToArray();

        var expanded = AcceptanceBalancedLaneExpander.Expand(manifest.InfrastructureTestLanes, classes,
            new Dictionary<string, double>(), pool);

        var owner = Assert.Single(AcceptanceLaneMembership.LanesIncluding(expanded.Lanes, name));
        Assert.StartsWith(Constraint + " ", owner.Name);
        Assert.Equal(configured.ExclusiveResourceKeys, owner.ExclusiveResourceKeys);
        Assert.DoesNotContain(name, expanded.Layout!.Lanes.SelectMany(lane => lane.Members));
        var residual = Assert.Single(expanded.Lanes, lane => lane.Name == pool.ResidualLaneName);
        Assert.Empty(AcceptanceLaneMembership.LanesIncluding([residual], name));
        Assert.False(AcceptanceSelectedExecutionPlan.IsDefaultFloor(owner.Name));
    }

    [Fact]
    public void FixedHistoryReducesMaximumWallAndKeepsPlacementDeterministic()
    {
        AcceptanceTestClassDescriptor[] classes = [
            new("Tests.Heavy", null, Constraint), new("Tests.A", null, Constraint),
            new("Tests.B", null, Constraint), new("Tests.C", null, Constraint),
            new("Tests.D", null, Constraint), new("Tests.Unconstrained", null)];
        var history = new Dictionary<string, double>
        {
            ["Tests.Heavy"] = 200, ["Tests.A"] = 60, ["Tests.B"] = 50,
            ["Tests.C"] = 40, ["Tests.D"] = 30
        };
        var single = ConstraintLanes(Expand(classes, history, 1));
        var balanced = ConstraintLanes(Expand(classes, history, 2));
        var repeated = ConstraintLanes(Expand(classes.Reverse().ToArray(), history, 2));

        // Legacy measurements use one slot, so the serial sum is also predicted wall.
        Assert.Equal(380, Assert.Single(single).EstimatedSerialSeconds);
        Assert.Equal(2, balanced.Length);
        Assert.Equal(200, balanced.Max(lane => lane.EstimatedSerialSeconds));
        Assert.Equal(380, balanced.Sum(lane => lane.EstimatedSerialSeconds));
        Assert.Equal(JsonSerializer.Serialize(balanced), JsonSerializer.Serialize(repeated));
        Assert.Equal("ProcessSpawning 1",
            Assert.Single(AcceptanceLaneMembership.LanesIncluding(balanced, "Tests.Heavy")).Name);
        Assert.All(balanced, lane => Assert.Equal([Resource], lane.ExclusiveResourceKeys));
        Assert.All(balanced, lane => Assert.True(AcceptanceCheckCommandBuilder.HasMtpTraitExclusion(
            AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray(), "Category=HostIntegration")));
        Assert.Empty(AcceptanceLaneMembership.LanesIncluding(balanced, "Tests.Unconstrained"));
    }

    [Fact]
    public void UnconfiguredConstraintReportsClassAndTraitValue()
    {
        const string className = "Tests.WrongConstraint";
        var error = Assert.Throws<InvalidDataException>(() =>
            Expand([new(className, null, "processspawning")], new Dictionary<string, double>(), 2));
        Assert.Contains(className, error.Message);
        Assert.Contains("unconfigured isolation constraint 'processspawning'", error.Message);
    }

    [Fact]
    public void HandWrittenOwnerConflictsWithTraitAndReportsClassAndLane()
    {
        const string className = "Tests.DoubleOwner";
        var hand = new AcceptanceTestLane("Hand lane", "FullyQualifiedName=Tests.DoubleOwner")
            { ExclusiveResourceKeys = ["machine:hand-resource"] };
        var error = Assert.Throws<InvalidDataException>(() => AcceptanceBalancedLaneExpander.Expand([hand],
            [new(className, null, Constraint)], new Dictionary<string, double>(), Settings(2)));
        Assert.Contains(className, error.Message);
        Assert.Contains(hand.Name, error.Message);
    }

    [Theory]
    [InlineData("""[{"name":"ProcessSpawning","laneCount":0}]""")]
    [InlineData("""[{"name":" ProcessSpawning"}]""")]
    [InlineData("""[{"name":"ProcessSpawning"},{"name":"processspawning"}]""")]
    [InlineData("""[{"name":"ProcessSpawning","exclusiveResourceKeys":["machine:a"," MACHINE:A "]}]""")]
    [InlineData("""[{"name":"Balanced"}]""")]
    public void InvalidConstraintSettingsAreRejected(string constraints)
    {
        var manifest = $$"""
            { "version": 1, "engine": { "balancedLanePool": { "isolationConstraints": {{constraints}} } } }
            """;
        Assert.Throws<InvalidDataException>(() => AcceptanceGateEngineSettings.Parse(manifest));
    }

    [Theory]
    [InlineData(null, "ProcessSpawning 1")]
    [InlineData("ProcessSpawning 1", null)]
    public void ConstraintLaneNameCannotCollideWithResidualOrHandLane(string? residual, string? hand)
    {
        var settings = new AcceptanceBalancedLanePoolSettings
        {
            ResidualLaneName = residual ?? "Residual",
            IsolationConstraints = [new() { Name = Constraint }]
        };
        AcceptanceTestLane[] lanes = hand is null ? [] : [new(hand, "FullyQualifiedName~HandTests")];
        Assert.Throws<InvalidDataException>(() => settings.Validate(lanes));
    }

    [Fact]
    public void UnconfiguredConstraintPropertyIsOmittedFromSerializedPool()
    {
        var settings = new AcceptanceBalancedLanePoolSettings();
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        Assert.False(serialized.RootElement.TryGetProperty(nameof(settings.IsolationConstraints), out _));
    }

    private static AcceptanceBalancedLanePoolSettings Settings(int count) => new()
    {
        IsolationConstraints = [new() { Name = Constraint, LaneCount = count, ExclusiveResourceKeys = [Resource] }],
        ExclusiveResourceKeys = ["machine:pool-resource"],
        TraitExclusions = ["Category!=HostIntegration"]
    };

    private static AcceptanceBalancedLaneExpansion Expand(AcceptanceTestClassDescriptor[] classes,
        IReadOnlyDictionary<string, double> history, int count) =>
        AcceptanceBalancedLaneExpander.Expand([], classes, history, Settings(count));

    private static AcceptanceTestLane[] ConstraintLanes(AcceptanceBalancedLaneExpansion expansion) =>
        expansion.Lanes.Where(lane => lane.Name.StartsWith(Constraint + " ", StringComparison.Ordinal)).ToArray();
}
