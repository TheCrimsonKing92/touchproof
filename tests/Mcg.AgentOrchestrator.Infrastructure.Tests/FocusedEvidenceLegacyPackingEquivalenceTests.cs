using Mcg.AgentOrchestrator.Infrastructure;
using Filter = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilter;

public sealed class FocusedEvidenceLegacyPackingEquivalenceTests
{
    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoHistory_ReturnsExactFilterOrderGroups(bool reverse)
    {
        var filters = "ABCDEF".Select(letter => "FullyQualifiedName~" + letter + new string('x', 399)).ToArray();
        var items = filters.Select(text =>
        {
            var name = text["FullyQualifiedName~".Length..];
            return (Target: "Infrastructure.Tests:" + text, Project,
                Filter: (Filter?)new Filter(text, text,
                    [new(name, text, GoalAcceptanceVerifier.FocusedEvidenceTokenKind.Class, name, name)]));
        }).ToArray();
        if (reverse) Array.Reverse(items);

        // Expected groups are fixed from the original ordinal, length-only algorithm,
        // rather than computed by comparing two calls to the current implementation.
        foreach (var history in new IClassDurationHistory[] { NoClassDurationHistory.Instance, new MissingHistory() })
        {
            var checks = FocusedEvidenceOverflowPacker.Pack(items, Project, history);
            Assert.Equal(3, checks.Count);
            for (var index = 0; index < checks.Count; index++)
            {
                var expected = filters.Skip(index * 2).Take(2).ToArray();
                var check = checks[index];
                Assert.Equal(Project, check.Project);
                Assert.Equal(expected.Select(text => "Infrastructure.Tests:" + text), check.Targets);
                Assert.Equal(string.Join('|', expected), check.Filter!.CanonicalText);
                Assert.Equal(string.Join("; ", expected.Select(text => "Infrastructure.Tests:" + text)),
                    check.Filter.OriginalToken);
                Assert.Equal(expected, check.Filter.Tokens.Select(token => token.CanonicalToken));
                Assert.Equal(expected, check.SelectionFilters.Select(filter => filter.CanonicalText));
                Assert.All(check.SelectionFilters, filter => Assert.Same(
                    items.Single(item => item.Filter!.CanonicalText == filter.CanonicalText).Filter, filter));
            }
        }

        Assert.Equal(new[] { string.Join('|', filters[..2]), string.Join('|', filters[2..4]), string.Join('|', filters[4..]) },
            FocusedEvidenceOverflowPacker.Pack(items, Project).Select(check => check.Filter!.CanonicalText));
    }

    private sealed class MissingHistory : IClassDurationHistory
    {
        public double? PredictSeconds(string key) => null;
    }
}
