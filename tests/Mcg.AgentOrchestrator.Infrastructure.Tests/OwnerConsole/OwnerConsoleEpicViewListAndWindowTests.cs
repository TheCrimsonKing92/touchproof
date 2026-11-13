using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Fixture = OwnerConsoleEpicViewFixture;
using static OwnerConsoleEpicViewFixture;

// Parallel-safe: per-test kernels, in-memory sources and an uninitialized instance application.
public sealed class OwnerConsoleEpicViewListAndWindowTests
{
    [Fact]
    public async Task EPressListsEveryEpicWithBuildCounts()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));

        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.Equal("EPICS · last 24 hours", fixture.View.EpicView.HeaderText);
        AssertSummaries(fixture, Now.AddHours(-24));
        Assert.Contains("landed 1 · in flight 3 (1 verifying, 2 active) · failed 1 · backlog 2 open / 1 done", fixture.View.EpicView.Lines);
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Alpha epic", StringComparison.Ordinal));
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Beta epic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WCyclesWindowAndCountsFollowBuild()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        foreach (var expected in new[]
        {
            (OwnerConsoleEpicWindow.Day, "last 24 hours", (DateTimeOffset?)Now.AddHours(-24), 1),
            (OwnerConsoleEpicWindow.Week, "last 7 days", (DateTimeOffset?)Now.AddDays(-7), 2),
            (OwnerConsoleEpicWindow.AllTime, "all time", (DateTimeOffset?)null, 3),
            (OwnerConsoleEpicWindow.Day, "last 24 hours", (DateTimeOffset?)Now.AddHours(-24), 1)
        })
        {
            Assert.Equal(expected.Item1, fixture.View.EpicView.TimeWindow);
            Assert.Contains(expected.Item2, fixture.View.EpicView.HeaderText);
            AssertSummaries(fixture, expected.Item3);
            Assert.StartsWith($"landed {expected.Item4} ·", fixture.View.EpicView.Lines[1]);
            await fixture.View.HandleKeyAsync(new Key('w'));
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task CommandAndKeyRestorePreviousPane(int paneValue, int tabs)
    {
        var pane = (OwnerConsolePane)paneValue;
        using var fixture = new Fixture();
        for (var i = 0; i < tabs; i++) await fixture.View.HandleKeyAsync(Key.Tab);
        Assert.Equal(pane, fixture.View.FocusedPane);
        await fixture.View.HandleKeyAsync(new Key('e'));
        var keyLines = fixture.View.EpicView.Lines.ToArray();
        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertClosedAndFocused(fixture.View, pane);

        await fixture.View.HandleKeyAsync(new Key(':'));
        var typedE = new Key('e');
        await fixture.View.HandleKeyAsync(typedE);
        Assert.False(typedE.Handled);
        Assert.False(fixture.View.EpicView.IsOpen);
        fixture.View.CommandLine.Text = ":epics";
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.True(fixture.View.EpicView.IsOpen);
        Assert.Equal(keyLines, fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertClosedAndFocused(fixture.View, pane);
        Assert.Contains("e epics", fixture.View.EpicHintText);
        Assert.Contains(":epics", fixture.View.EpicHintText);
        await fixture.View.HandleKeyAsync(new Key('?'));
        var help = Assert.Single(fixture.Dialogs.Texts).Text;
        Assert.Contains(help.Split('\n'), line => line.StartsWith("e: ", StringComparison.Ordinal));
        Assert.Contains(help.Split('\n'), line => line.StartsWith(":epics: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyEpicRemainsVisibleAndEnterOnEmptyListDoesNothing()
    {
        using var fixture = new Fixture();
        fixture.Source.Epics.Clear();
        fixture.Source.Epics.Add(new("empty", "Empty epic", null, Now, "test", Now, "test"));
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("landed 0 · in flight 0 (0 verifying, 0 active) · failed 0 · backlog 0 open / 0 done", fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Contains("(no plan of record)", fixture.View.EpicView.Lines);
        await fixture.View.HandleKeyAsync(Key.Esc);
        Assert.False(fixture.View.EpicView.ShowingDetail);
        fixture.Source.Epics.Clear();
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        var calls = fixture.Source.Calls;
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Equal(calls, fixture.Source.Calls);
        Assert.False(fixture.View.EpicView.ShowingDetail);
        Assert.Equal(["No epics are defined yet. Create one with epic-add."], fixture.View.EpicView.Lines);
    }

    [Fact]
    public async Task Open_ZeroEpics_ShowsCreationHint()
    {
        using var fixture = new Fixture();
        fixture.Source.Epics.Clear();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Equal(["No epics are defined yet. Create one with epic-add."], fixture.View.EpicView.Lines);
        Assert.Contains("updated 01:00:00", fixture.View.EpicView.HeaderLine);
        Assert.Equal(1, fixture.Source.Calls);
    }

    [Theory]
    [InlineData(false, "last 24 hours")]
    [InlineData(true, "last 7 days")]
    public async Task Window_OldActivity_ShowsWidenHintAboveEpics(bool week, string label)
    {
        using var fixture = new Fixture();
        fixture.Source.Goals.RemoveAll(goal => goal.Id != "a-oldest");
        await fixture.View.HandleKeyAsync(new Key('e'));
        if (week) await fixture.View.HandleKeyAsync(new Key('w'));
        var dialog = fixture.View.EpicView;
        Assert.Equal($"No epic activity in the {label}; press w to widen.", dialog.Lines[0]);
        Assert.Equal("> Alpha epic", dialog.Lines[1]);
        Assert.Contains("  Beta epic", dialog.Lines);
        Assert.Equal(1, dialog.BodyPane.SelectedItem);
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal("> Beta epic", dialog.Lines[4]);
        Assert.Equal(4, dialog.BodyPane.SelectedItem);
        if (!week) await fixture.View.HandleKeyAsync(new Key('w'));
        await fixture.View.HandleKeyAsync(new Key('w'));
        Assert.DoesNotContain("No epic activity", string.Join(" ", dialog.Lines));
        AssertSummaries(fixture, null);
    }
}
