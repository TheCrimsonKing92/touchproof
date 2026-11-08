using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Text;

// Pure model fixtures; safe to run in parallel.
public sealed class OwnerFleetDiagramWidthTests
{
    private const string Lane = "infrastructure tests: Goal lifecycle commands";

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    [InlineData(40)]
    public void LaneRowsTruncateNamesAndPreserveNumericCells(int width)
    {
        var text = OwnerFleetDiagram.Render(Model(Lane), width);
        var rows = text.Split('\n');
        Assert.All(rows, row => Assert.Equal(width, row.GetColumns()));
        var row = Assert.Single(rows.Where(row => row.Contains("infrastructure", StringComparison.Ordinal)));
        Assert.Matches(@" +12 +47\.3 +- +-$", Content(row));
        Assert.DoesNotContain("median", text);
        Assert.DoesNotContain(rows, row => row.StartsWith("│   solo", StringComparison.Ordinal));
        if (width == 120) Assert.Contains(Lane, row);
        else Assert.Contains('…', row);
        Assert.Single(rows.Where(row => row.Contains("abcdefghijkl", StringComparison.Ordinal)));
    }

    [Fact]
    public void LongWordIsOneTruncatedRow()
    {
        var word = new string('x', 60);
        var rows = OwnerFleetDiagram.Render(Model(word), 40).Split('\n');
        Assert.All(rows, row => Assert.Equal(40, row.GetColumns()));
        var row = Assert.Single(rows.Where(row => row.Contains("xxxxxxxx", StringComparison.Ordinal)));
        Assert.Contains('…', row);
        Assert.Matches(@" +12 +47\.3 +- +-$", Content(row));
    }

    [Fact]
    public void LaneNamesCleanControlsAndFitWideRunes()
    {
        var text = OwnerFleetDiagram.Render(Model("a\u001b\tb" + new string('界', 40)), 40);
        Assert.Contains("a  b", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\t', text);
        Assert.All(text.Split('\n'), row => Assert.Equal(40, row.GetColumns()));
    }

    [Fact]
    public void BelowMinimumStillThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerFleetDiagram.Render(Model(Lane), 39));

    private static string Content(string row) => row[2..^2].Trim();

    private static OwnerFleetViewModel Model(string lane) => OwnerFleetViewModelBuilder.Build(
        new([new("executor", 0, new Dictionary<string, int>(), null, null, [], null, null, 0, 0,
            [new(lane, 12, 47.3, 47.3, null, null), new("abcdefghijklmnopqrst", 1, 2, 2, null, null)])], 0, 0),
        true, TimeZoneInfo.Utc);
}
