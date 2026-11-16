using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class PreTesterReverseDependencySelectionStepTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WalkRecordsOnlyExecutedReverseDependencyParts(bool missingPath)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.WithCostHistory();
        if (missingPath) File.Delete(Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.Source));
        var recorded = new List<string>();
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero, recorded.Add);
        using var active = ledger.Activate();

        var actual = ConductorTickStepLedger.Measure("walk-pretester-reverse-dependency", () =>
            PreTesterReverseDependencySelections.Select(fixture.Root,
                [PreTesterReverseDependencySelectionFixture.Source], _ => { }));

        Assert.Equal(missingPath ? 0 : 1, actual.Selections.Count);
        var parts = new[] { "read-selection", "resolve-class-names", "evaluate-cost-rule" }
            .Select(part => "walk-pretester-reverse-dependency-" + part).ToArray();
        var line = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero);
        var entries = line["walk_steps=".Length..].Split(' ')[0].Split(',')
            .Select(entry => entry.Split(':')).ToDictionary(entry => entry[0]);
        foreach (var name in parts)
        {
            Assert.Equal(missingPath ? 0 : 1, recorded.Count(item => item == name));
            Assert.Equal(missingPath ? "0" : "1", entries[name][3]);
        }
        Assert.Equal("1", entries["walk-pretester-reverse-dependency"][3]);
        Assert.Equal(missingPath ? [] : parts, recorded.Where(parts.Contains));
    }
}
