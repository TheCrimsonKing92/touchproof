using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: per-test directories, settings, fake output and injected clock.
public sealed class OwnerConsoleAlertsSettingsTabTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewPlaysOnceWithoutSaving_SystemSelectionClearsFile(bool muted)
    {
        using var context = new OwnerConsoleSettingsTestContext(muted ? "[\"calm\"]" : "[]");
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        view.HandleKey(Key.CursorDown);
        view.HandleKey(Key.CursorDown);
        var bytes = File.ReadAllBytes(context.Path);
        var snapshot = context.Settings.Current;
        view.HandleKey(new Key('p'));
        Assert.Equal([context.Clip("calm")], context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
        Assert.Same(snapshot, context.Settings.Current);
        Assert.Contains(view.Lines, line => line.StartsWith("calm: old.wav", StringComparison.Ordinal));

        view.HandleKey(new Key('s'));
        Assert.False(context.Saved.ContainsKey("calmSoundFile"));
        var expected = context.Original.DeepClone().AsObject();
        expected.Remove("calmSoundFile");
        Assert.True(JsonNode.DeepEquals(expected, context.Saved));
        Assert.Null(context.Settings.Current.CalmSoundFile);
        Assert.Equal(snapshot.BellTiers, context.Settings.Current.BellTiers);
        Assert.Contains(view.Lines, line => line.StartsWith("calm: system sound", StringComparison.Ordinal));
        context.Audio.Files.Clear();
        context.Notifier.Notify(OwnerConsoleNotificationTier.Calm);
        Assert.Empty(context.Audio.Files);
        Assert.Equal([0x40u], context.Audio.SystemSounds);
    }

    [Theory]
    [InlineData("attentionThresholdMinutes", "5")]
    [InlineData("staleTickThresholdMinutes", "5")]
    [InlineData("calmCoalesceSeconds", "2")]
    public void ValidThreshold_SavesAndChangesNextDetection(string key, string value)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        var baseline = OwnerConsoleSettingsTestContext.Model(running: key == "staleTickThresholdMinutes");
        if (key == "attentionThresholdMinutes") baseline = baseline with
        { Decisions = [OwnerConsoleSettingsTestContext.Decision(context.Clock.Now.AddMinutes(-4))] };
        if (key == "staleTickThresholdMinutes") baseline = baseline with
        { Status = baseline.Status with { LatestTickAt = context.Clock.Now.AddMinutes(-4) } };
        context.Controller.Apply(baseline);
        if (key == "calmCoalesceSeconds") context.Controller.Apply(OwnerConsoleSettingsTestContext.Model(1));
        context.Audio.Files.Clear();
        context.Audio.SystemSounds.Clear();
        OwnerConsoleSettingsTestContext.Edit(view, key, value);
        Assert.False(tab.Editing);
        Assert.Contains("saved " + key, tab.Message);
        var expected = context.Original.DeepClone().AsObject();
        expected[key] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(JsonNode.DeepEquals(expected, context.Saved));
        Assert.Contains(view.Lines, line => line.StartsWith(key + ": " + value, StringComparison.Ordinal) && line.Contains("default"));

        context.Clock.Now += key == "calmCoalesceSeconds" ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(2);
        context.Controller.Apply(key == "calmCoalesceSeconds" ? OwnerConsoleSettingsTestContext.Model(2) : baseline);
        if (key == "calmCoalesceSeconds") Assert.Equal(["old.wav"], context.Audio.Files);
        else Assert.Equal([key == "attentionThresholdMinutes" ? 0x30u : 0x10u], context.Audio.SystemSounds);
        Assert.Equal(OwnerConsoleNotificationSettings.Load(context.Path), context.Settings.Current);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData("99999999999")]
    [InlineData("+2")]
    public void InvalidThreshold_RefusesAndLeavesFileByteIdentical(string input)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        var bytes = File.ReadAllBytes(context.Path);
        var snapshot = context.Settings.Current;
        OwnerConsoleSettingsTestContext.Edit(view, "attentionThresholdMinutes", input);
        Assert.True(tab.Editing);
        Assert.Contains("positive whole number", tab.Message);
        Assert.Contains("positive whole number", tab.StatusText);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
        Assert.Same(snapshot, context.Settings.Current);
    }

    [Fact]
    public void EditEscapeDiscardsInput_AndBackspaceCanCorrectIt()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Activity);
        var bytes = File.ReadAllBytes(context.Path);
        OwnerConsoleSettingsTestContext.Highlight(view, "calmCoalesceSeconds:");
        view.HandleKey(Key.Enter);
        view.HandleKey(new Key('q')); // q is editor input, not a close action.
        Assert.True(view.IsOpen);
        Assert.False(view.HandleKey(Key.Esc));
        Assert.False(tab.Editing);
        Assert.True(view.IsOpen);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
        view.HandleKey(Key.Enter);
        view.HandleKey(new Key('5'));
        view.HandleKey(new Key('0'));
        view.HandleKey(Key.Backspace);
        view.HandleKey(Key.Enter);
        Assert.Equal(5, context.Saved["calmCoalesceSeconds"]!.GetValue<int>());
        Assert.False(tab.Editing);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("system")]
    [InlineData("mute")]
    [InlineData("threshold")]
    public void SaveFailure_KeepsCommittedStateAndCleansTemporaryFile(string action)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        File.Delete(context.Path);
        Directory.CreateDirectory(context.Path); // Deterministically refuses atomic replacement.
        var snapshot = context.Settings.Current;
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        if (action == "threshold") OwnerConsoleSettingsTestContext.Edit(view, "attentionThresholdMinutes", "5");
        else if (action == "select") { view.HandleKey(Key.CursorDown); view.HandleKey(Key.Enter); }
        else view.HandleKey(new Key(action == "system" ? 's' : 'm'));
        Assert.Contains("failed:", tab.Message);
        Assert.Same(snapshot, context.Settings.Current);
        Assert.Contains(view.Lines, line => line.StartsWith("calm: old.wav (on)", StringComparison.Ordinal));
        Assert.True(Directory.Exists(context.Path));
        Assert.Empty(Directory.GetFiles(context.Root));
        Assert.Empty(context.Audio.Files);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("error")]
    public void UnavailableLibrary_ReportsStateAndKeepsSystemAndMuteUsable(string state)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        context.FolderExists = state != "missing";
        context.Listing = _ => state == "error" ? throw new IOException("listing refused") : [];
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        Assert.Contains(view.Lines, line => line.Contains("(no clips)"));
        if (state == "missing") Assert.Contains(view.Lines, line => line.Contains("library folder not found:"));
        if (state == "error") Assert.Contains(view.Lines, line => line.Contains("listing refused"));
        view.HandleKey(new Key('s'));
        Assert.Null(context.Settings.Current.CalmSoundFile);
        view.HandleKey(new Key('m'));
        Assert.False(context.Settings.Current.BellTiers.Calm);
    }

    [Fact]
    public void FailedPreview_ReportsFailureWithoutChangingSelection()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        context.Audio.PlaySucceeds = false;
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Decisions);
        view.HandleKey(Key.CursorDown);
        var bytes = File.ReadAllBytes(context.Path);
        view.HandleKey(new Key('p'));
        Assert.Equal("could not play first.wav", tab.Message);
        Assert.Single(context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
    }
}
