using Mcg.AgentOrchestrator.Infrastructure;
using Filter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;
using PlannedCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidencePlannedCheck;

public sealed class FocusedEvidenceCostBalancedPackingTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Fact]
    public void KnownCosts_ThreeGroupsUseLongestFirstAndImproveLegacyMaximum()
    {
        var names = "ABCDEF".Select(letter => letter + new string('x', 399)).ToArray();
        var items = names.Select(Item).ToArray();
        double[] seconds = [100, 90, 80, 30, 20, 10];
        var history = new History(names.Select((name, index) =>
            KeyValuePair.Create("Infrastructure.Tests:" + name, seconds[index])).ToDictionary());
        var legacy = FocusedEvidenceOverflowPacker.Pack(items, Project);
        var balanced = FocusedEvidenceOverflowPacker.Pack(items, Project, history);

        // Two filters fit; three cannot. The legacy target is exactly three groups.
        Assert.True(string.Join('|', items.Take(2).Select(item => item.Filter!.CanonicalText)).Length <=
            GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        Assert.True(string.Join('|', items.Take(3).Select(item => item.Filter!.CanonicalText)).Length >
            GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength);
        Assert.Equal(new[] { "AB", "CD", "EF" }, Membership(legacy));
        Assert.Equal(3, balanced.Count);
        Assert.Equal(new[] { "AF", "BE", "CD" }, Membership(balanced));
        Assert.All(balanced, check => Assert.InRange(check.Filter!.CanonicalText.Length, 1,
            GoalAcceptanceVerifier.MaxFocusedEvidenceFilterLength));
        Assert.Equal(items.Select(item => item.Target).Order(StringComparer.Ordinal),
            balanced.SelectMany(check => check.Targets).Order(StringComparer.Ordinal));
        Assert.Equal(names.Order(StringComparer.Ordinal), balanced.SelectMany(check => check.SelectionFilters)
            .Select(filter => Assert.Single(filter.Tokens).ContainingClass).Order(StringComparer.Ordinal));

        var model = new FocusedEvidenceGroupCostModel(Project, items.SelectMany(item => item.Filter!.Tokens), history);
        Assert.Equal(new[] { 110d, 110d, 110d }, balanced.Select(Cost));
        Assert.Equal(190d, legacy.Max(Cost));
        Assert.True(balanced.Max(Cost) <= legacy.Max(Cost));

        // A fixed permutation exercises order independence without a random seed.
        var shuffled = new[] { 3, 0, 5, 2, 4, 1 }.Select(index => items[index]).ToArray();
        var repacked = FocusedEvidenceOverflowPacker.Pack(shuffled, Project, history);
        Assert.Equal(balanced.Select(check => check.Filter!.CanonicalText),
            repacked.Select(check => check.Filter!.CanonicalText));
        Assert.Equal(balanced.Select(check => string.Join(';', check.Targets)),
            repacked.Select(check => string.Join(';', check.Targets)));
        Assert.Equal(balanced.Select(Cost), repacked.Select(Cost));

        double Cost(PlannedCheck check) =>
            FocusedEvidenceGroupCostModel.Sum(check.SelectionFilters.Select(filter => model.PredictSeconds(filter.Tokens)))!.Value;
    }

    private static (string Target, string Project, Filter? Filter) Item(string name)
    {
        var text = "FullyQualifiedName~" + name;
        return ("Infrastructure.Tests:" + text, Project,
            new Filter(text, text, [new(name, text, GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class, name, name)]));
    }

    private static IEnumerable<string> Membership(IEnumerable<PlannedCheck> checks) => checks.Select(check =>
        string.Concat(check.SelectionFilters.Select(filter => Assert.Single(filter.Tokens).ContainingClass[0])));

    private sealed class History(Dictionary<string, double> values) : IClassDurationHistory
    {
        public double? PredictSeconds(string key) => values.TryGetValue(key, out var seconds) ? seconds : null;
    }
}
