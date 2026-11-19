using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

public sealed class FocusedReverseDependentLaneSplitterTests
{
    private const string FocusedName = "focused reverse-dependent infrastructure tests";
    private static readonly string[] Changes = ["src/Mcg.AgentOrchestrator.Core/Application/Example.cs"];
    private static readonly PolicyShardPlan Policy = PolicyShardPlan.NotApplicable("fixture");
    private static readonly AcceptanceTestLane[] Pool =
    [
        new("Balanced 1", "FullyQualifiedName=Tests.First"),
        new("Balanced 2", "FullyQualifiedName=Tests.Second")
    ];
    private static readonly Check Broad = new()
    {
        Name = "infrastructure tests", Type = "dotnet-test", Project = InfrastructureTestsProject
    };

    [Fact]
    public void OversizedSelectionFindsAValidPartitionAfterFirstFitFails()
    {
        var timings = new Dictionary<string, double>
        {
            ["Alpha"] = 4, ["Beta"] = 4, ["Gamma"] = 3, ["Delta"] = 3, ["Epsilon"] = 3, ["Zeta"] = 3
        };
        var focused = Focused([.. timings.Keys]);
        var expansion = FocusedReverseDependentLaneSplitter.Plan(focused, Broad, Pool, 10, 10,
            new AcceptanceClassDurationHistoryAdapter(timings), [.. timings.Keys],
            resourceKeysForClass: name => name == "Alpha" ? ["host:Alpha"] : []);
        var checks = Schedule(focused, expansion);

        Assert.Null(expansion.FullGateReason);
        Assert.Equal(new[] { $"{FocusedName}: Balanced 1", $"{FocusedName}: Balanced 2" },
            checks.Select(check => check.Name));
        var selectedClasses = checks.SelectMany(Classes).Order(StringComparer.Ordinal);
        Assert.Equal(timings.Keys.Order(StringComparer.Ordinal), selectedClasses);
        Assert.All(checks, shard =>
        {
            Assert.True(shard.IsResolvedInfrastructureLane);
            Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(shard, out var partitionId, out var filter));
            Assert.Equal(shard.Name.EndsWith("1", StringComparison.Ordinal) ? "balanced-1" : "balanced-2", partitionId);
            Assert.Equal(shard.Arguments[3], filter);
            var estimate = Classes(shard).Sum(name => timings[name]);
            Assert.Equal(10, estimate);
            Assert.Equal(estimate, shard.EstimatedSerialSeconds);
            Assert.Equal(focused.Project, shard.Project);
            Assert.Equal(focused.Runner, shard.Runner);
            Assert.Equal(focused.Reports, shard.Reports);
            Assert.Equal(focused.Arguments.Take(3), shard.Arguments.Take(3));
            Assert.NotEqual(focused.Arguments[3], shard.Arguments[3]);
        });
        var keyedShard = Assert.Single(checks, shard => Classes(shard).Contains("Alpha"));
        Assert.Contains("host:Alpha", keyedShard.ExclusiveResourceKeys);
    }

    [Fact]
    public void OversizedSelectionFindsTheFeasibleFourLanePacking()
    {
        int[] seconds = [14, 19, 72, 15, 34, 52, 22, 12, 3, 5, 8, 32, 20, 16, 4, 21, 11, 40, 24, 46, 10];
        var timings = seconds.Select((value, index) => (Name: $"TestClass{index:D2}", Seconds: (double)value))
            .ToDictionary(item => item.Name, item => item.Seconds);
        var focused = Focused([.. timings.Keys]);
        AcceptanceTestLane[] pool = [.. Pool, new("Balanced 3", ""), new("Balanced 4", "")];
        var expansion = FocusedReverseDependentLaneSplitter.Plan(focused, Broad, pool, 120, 120,
            new AcceptanceClassDurationHistoryAdapter(timings), [.. timings.Keys]);

        Assert.Null(expansion.FullGateReason);
        Assert.Equal(4, expansion.Checks.Count);
        Assert.Equal(timings.Keys.Order(StringComparer.Ordinal),
            expansion.Checks.SelectMany(Classes).Order(StringComparer.Ordinal));
        Assert.All(expansion.Checks, shard =>
        {
            Assert.Equal(120d, Classes(shard).Sum(name => timings[name]));
            Assert.Equal(120d, shard.EstimatedSerialSeconds);
        });
    }

    [Fact]
    public void OverlappingSelectorsScheduleEachActualClassOnce()
    {
        const string shortName = "ConductorDriverTests";
        const string longerName = shortName + "GateFailureLandingAttribution";
        string[] inventory = ["Tests." + shortName, "Tests." + longerName, "Tests.Other"];
        // The short selector also executes the longer class. The original check therefore
        // costs 12 seconds even when the planner emits only the short selector.
        var focused = Focused(shortName);
        var history = new AcceptanceClassDurationHistoryAdapter(new Dictionary<string, double>
        {
            [inventory[0]] = 6, [inventory[1]] = 6
        });
        var expansion = FocusedReverseDependentLaneSplitter.Plan(focused, Broad, Pool, 10, 10, history, inventory);
        var checks = Schedule(focused, expansion);

        Assert.Null(expansion.FullGateReason);
        Assert.Equal(2, checks.Count);
        var matches = checks.Select(shard =>
        {
            Assert.True(shard.IsResolvedInfrastructureLane);
            Assert.True(GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(shard, out _, out var filter));
            var matcher = AcceptanceLaneMembership.CreateClassMatcher(new AcceptanceTestLane(shard.Name, filter));
            return Assert.Single(inventory.Where(matcher));
        }).Order(StringComparer.Ordinal);
        Assert.Equal(inventory.Take(2), matches);
        Assert.All(checks, shard => Assert.Equal(6d, shard.EstimatedSerialSeconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnpricedClassSchedulesFullGateWithKnownSubtotal(double? unavailable)
    {
        var timings = new Dictionary<string, double> { ["Estimated"] = 7 };
        if (unavailable is { } value) timings.Add("Unestimated", value);
        var focused = Focused("Estimated", "Unestimated");

        AssertFullGate(focused, new AcceptanceClassDurationHistoryAdapter(timings), null,
            "total estimate unknown", "known subtotal 7 seconds", "no usable estimate for Unestimated");
    }

    [Theory]
    [InlineData(8, 8, 8, 0, "estimated 24 seconds", "available pool budget 20 seconds")]
    [InlineData(11, 3, 3, 0, "estimated 17 seconds", "Alpha estimates 11 seconds")]
    [InlineData(6, 6, 6, 0, "estimated 18 seconds", "cannot be assigned within the available lane budgets")]
    [InlineData(6, 6, 4, 5, "estimated 16 seconds", "available pool budget 15 seconds")]
    public void InfeasibleSelectionSchedulesFullGateWithBudgetEvidence(
        double alpha, double beta, double gamma, double occupied, string estimate, string detail)
    {
        var focused = Focused("Alpha", "Beta", "Gamma");
        var history = new AcceptanceClassDurationHistoryAdapter(new Dictionary<string, double>
        {
            ["Alpha"] = alpha, ["Beta"] = beta, ["Gamma"] = gamma
        });
        AssertFullGate(focused, history, new Dictionary<string, double> { ["Balanced 1"] = occupied }, estimate, detail);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void SelectionWithinBudgetPreservesOriginalCheckAndFilter(double beta)
    {
        var focused = Focused("Beta", "Alpha");
        var history = new AcceptanceClassDurationHistoryAdapter(new Dictionary<string, double>
        {
            ["Alpha"] = 4, ["Beta"] = beta
        });
        var expansion = FocusedReverseDependentLaneSplitter.Plan(focused, Broad, Pool, 10, 10, history, ["Alpha", "Beta"]);
        var check = Assert.Single(Schedule(focused, expansion));

        Assert.Null(expansion.FullGateReason);
        Assert.Same(focused, check);
        Assert.Equal(FocusedName, check.Name);
        Assert.Equal("FullyQualifiedName~Beta|FullyQualifiedName~Alpha", check.Arguments[3]);
    }

    [Fact]
    public void ExecutionUsesConfiguredDestinationBudgetAndAccountsForLaneWork()
    {
        var focused = Focused("Alpha", "Beta");
        var history = new AcceptanceClassDurationHistoryAdapter(new Dictionary<string, double>
        {
            ["Alpha"] = 50, ["Beta"] = 45
        });
        var occupied = new Check
        {
            Name = "additional infrastructure tests: Balanced 1", Type = "dotnet-test",
            Project = InfrastructureTestsProject, Arguments = ["--filter", "FullyQualifiedName~Extra"],
            EstimatedSerialSeconds = 20
        };
        var layout = new AcceptanceBalancedLaneLayout(5, 1,
            Pool.Select(lane => new AcceptanceBalancedLanePrediction(lane.Name, 50, [])).ToArray(), "Residual", []);
        var lanes = new AcceptanceBalancedLaneExpansion([.. Pool, new("Other lane", "FullyQualifiedName=Tests.Other")], layout);
        var settings = new AcceptanceGateEngineSettings { Timeouts = new() { DefaultMinutes = 1 } };
        var expansion = FocusedReverseDependentLaneSplitter.Resolve([focused], [Broad, occupied], Policy,
            lanes, settings, "fixture", history: history, inventory: ["Alpha", "Beta"]);

        Assert.NotNull(expansion);
        Assert.Contains("estimated 95 seconds", expansion.FullGateReason);
        Assert.Contains("destination lane budget 60 seconds", expansion.FullGateReason);
        Assert.Contains("available pool budget 100 seconds", expansion.FullGateReason);
        Assert.Contains("cannot be assigned within the available lane budgets", expansion.FullGateReason);
    }

    private static void AssertFullGate(
        Check focused, IClassDurationHistory history, IReadOnlyDictionary<string, double>? occupied, params string[] reasonParts)
    {
        var expansion = FocusedReverseDependentLaneSplitter.Plan(focused, Broad, Pool, 10, 10, history,
            Classes(focused).ToArray(), occupied);
        var checks = Schedule(focused, expansion);
        var receipt = expansion.Observe(new AcceptanceGateLanePlan("fixture", "attempt", [], []), checks);

        Assert.Equal(Pool.Select(lane => $"{Broad.Name}: {lane.Name}"), checks.Select(check => check.Name));
        Assert.Equal(Pool.Select(lane => lane.Filter), checks.Select(check => check.Arguments[1]));
        Assert.DoesNotContain(checks, check => check.Name.StartsWith(FocusedName, StringComparison.Ordinal));
        Assert.Equal("full-gate", receipt.SelectedExecution?.Decision);
        var reason = Assert.IsType<string>(receipt.SelectedExecution?.FullGateReason);
        Assert.Contains("overflow", reason);
        Assert.Contains("focused lane budget 10 seconds", reason);
        Assert.All(reasonParts, expected => Assert.Contains(expected, reason));
    }

    private static IReadOnlyList<Check> Schedule(Check focused, FocusedReverseDependentLaneSplitter.Expansion expansion) =>
        GoalAcceptanceVerifier.ExpandBroadInfrastructureChecks(
            GoalAcceptanceVerifier.BuildPolicyEffectiveChecks("fixture", [Broad], Changes, [focused], Policy, Pool, expansion), Pool);

    private static Check Focused(params string[] classes) => new()
    {
        Name = FocusedName, Type = "dotnet-test", Project = InfrastructureTestsProject, Runner = "mtp",
        Arguments = ["--verbosity", "minimal", "--filter", string.Join('|', classes.Select(name => $"FullyQualifiedName~{name}"))],
        Reports = ["fixture.trx"]
    };

    private static IEnumerable<string> Classes(Check check) =>
        check.Arguments[3].Split('|').Select(token => token["FullyQualifiedName=".Length..]);
}
