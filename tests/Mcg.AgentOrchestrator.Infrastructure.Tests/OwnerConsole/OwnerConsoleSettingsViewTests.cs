using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;

// Parallel-safe: isolated settings and injected audio/listing; applications are never initialized.
public sealed class OwnerConsoleSettingsViewTests
{
    [Theory]
    [InlineData("calm")]
    [InlineData("attention")]
    [InlineData("alarm")]
    public void SelectThroughKeys_PreservesDocumentAndChangesNextAlert(string tier)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        var baseline = OwnerConsoleSettingsTestContext.Model(running: tier == "alarm");
        if (tier == "attention") baseline = baseline with
        { Decisions = [OwnerConsoleSettingsTestContext.Decision(context.Clock.Now.AddMinutes(-29))] };
        context.Controller.Apply(baseline);
        Assert.Empty(context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);

        var expected = context.Original.DeepClone().AsObject();
        expected["addedAfterStartup"] = new JsonArray("keep", 2);
        File.WriteAllText(context.Path, expected.ToJsonString());
        view.Open(OwnerConsolePane.Board);
        OwnerConsoleSettingsTestContext.Highlight(view, tier + ":");
        view.HandleKey(Key.CursorDown); // The shared listing orders first.wav before two words.wav.
        view.HandleKey(Key.CursorDown);
        view.HandleKey(Key.Enter);
        expected[tier + "SoundFile"] = context.Clip(tier);
        Assert.True(JsonNode.DeepEquals(expected, context.Saved));
        Assert.Contains(view.Lines, line => line.StartsWith(tier + ": two words.wav", StringComparison.Ordinal));
        Assert.Empty(context.Audio.Files); // Selection itself is silent.
        Assert.Empty(context.Audio.SystemSounds);

        if (tier == "attention") context.Clock.Now += TimeSpan.FromMinutes(2);
        var next = tier == "calm" ? OwnerConsoleSettingsTestContext.Model(1) :
            tier == "alarm" ? baseline with { Status = baseline.Status with { ConductorRunning = ConductorLiveness.NotRunning } } : baseline;
        context.Controller.Apply(next);
        if (tier == "alarm")
        {
            Assert.Empty(context.Audio.Files);
            Assert.Empty(context.Audio.SystemSounds);
            context.Clock.Now += TimeSpan.FromSeconds(61); // Unplanned stops ring after the grace period.
            context.Controller.Apply(next);
        }
        Assert.Equal([context.Clip(tier)], context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
    }

    [Fact]
    public async Task CommandOpensModal_KeysCloseAndRestorePriorPane()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        using IApplication app = Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, context.Controller, () => Task.CompletedTask);
        view.Render(OwnerConsoleSettingsTestContext.Model());
        await view.HandleKeyAsync(Key.Tab);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
        await view.ActAsync(_ => context.Controller.RunCommandAsync(":settings"));
        Assert.True(view.SettingsView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.SettingsView.ReturnPane);
        Assert.Empty(context.Output.Texts); // No empty Command result dialog before the view.
        await view.HandleKeyAsync(new Key('m'));
        Assert.Contains(view.SettingsView.Lines, line => line.StartsWith("calm: old.wav (muted)", StringComparison.Ordinal));
        context.Controller.Apply(OwnerConsoleSettingsTestContext.Model(1));
        Assert.Empty(context.Audio.Files); // Notification processing continues and honors the modal's mute.
        await view.HandleKeyAsync(new Key('q'));
        Assert.False(view.SettingsView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, view.FocusedPane);
        Assert.False(context.Controller.QuitRequested);
    }

    [Fact]
    public void RegisteredTabs_SwitchWithoutChangingFrame_AndEditingOwnsEscape()
    {
        var first = new Tab("First");
        var second = new Tab("Second");
        using var view = new OwnerConsoleSettingsView([first, second]);
        view.Open(OwnerConsolePane.Activity);
        Assert.Equal(["First"], view.Lines);
        view.HandleKey(Key.CursorRight);
        Assert.Equal(["Second"], view.Lines);
        second.Editing = true;
        Assert.False(view.HandleKey(Key.Esc));
        Assert.True(view.IsOpen);
        Assert.False(second.Editing);
        view.HandleKey(Key.CursorLeft);
        Assert.Equal(0, view.ActiveTab);
        Assert.True(view.HandleKey(Key.Esc));
    }

    private sealed class Tab(string title) : IOwnerConsoleSettingsTab
    {
        public string Title => title;
        public IReadOnlyList<string> Lines => [title];
        public string KeyHint => "test";
        public string StatusText => "";
        public bool Editing { get; set; }
        public int SelectedLine => 0;
        public void HandleKey(Key key) { if (key == Key.Esc) Editing = false; }
        public void Refresh() { }
    }
}
