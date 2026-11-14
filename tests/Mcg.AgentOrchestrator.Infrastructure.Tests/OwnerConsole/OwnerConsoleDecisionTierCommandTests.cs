using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: unique settings directories, fake playback and no global GUI state.
public sealed class OwnerConsoleDecisionTierCommandTests
{
    [Theory]
    [InlineData("1", "first.wav")]
    [InlineData("TWO WORDS.WAV", "two words.wav")]
    public async Task SoundSet_Decision_RoundTripsClipAndPreservesAlarm(string selector, string name)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var original = context.Original.DeepClone().AsObject();
        original["alarmSoundFile"] = "alarm.wav";
        File.WriteAllText(context.Path, original.ToJsonString());
        context.Restart();
        var clip = Path.Combine(context.Library, "decision", name);

        await context.Controller.RunCommandAsync(" :SoUnD SeT DeCiSiOn " + selector);

        Assert.Equal("sound set decision: " + name, context.Output.Texts[^1].Trim());
        Assert.Equal(clip, context.Settings.Current.DecisionSoundFile);
        Assert.Equal("alarm.wav", context.Settings.Current.AlarmSoundFile);
        var expected = original.DeepClone().AsObject();
        expected["decisionSoundFile"] = clip;
        Assert.True(JsonNode.DeepEquals(expected, context.Saved));
        Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
        context.Restart();
        Assert.Equal(clip, context.Settings.Current.DecisionSoundFile);

        await context.Controller.RunCommandAsync("sound play decision");
        Assert.Equal([clip], context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        await context.Controller.RunCommandAsync("sound set decision system");
        Assert.Null(context.Settings.Current.DecisionSoundFile);
        Assert.Equal("alarm.wav", context.Settings.Current.AlarmSoundFile);
        Assert.True(JsonNode.DeepEquals(original, context.Saved));
        Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
    }

    [Theory]
    [InlineData("1", "first.wav")]
    [InlineData("\"two words.wav\"", "two words.wav")]
    public async Task SoundPlay_ExplicitDecisionClip_PreviewsWithoutSaving(string selector, string name)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var bytes = File.ReadAllBytes(context.Path);
        var snapshot = context.Settings.Current;

        await context.Controller.RunCommandAsync("sound play decision " + selector);

        Assert.Equal([Path.Combine(context.Library, "decision", name)], context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
        Assert.Same(snapshot, context.Settings.Current);
    }

    [Fact]
    public async Task BellDecision_OffThenOn_PersistsAndPreservesOtherFlags()
    {
        using var context = new OwnerConsoleSettingsTestContext();

        await context.Controller.RunCommandAsync(" :BeLl DeCiSiOn OFF ");

        Assert.Equal("bell decision off", context.Output.Texts[^1].Trim());
        Assert.Equal(new(true, true, true, false), context.Settings.Current.BellTiers);
        Assert.Equal("calm on, attention on, alarm on, decision off", context.Controller.BellStatusText);
        Assert.Equal(["decision"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
        context.Restart();
        Assert.False(context.Settings.Current.BellTiers.Decision);
        await context.Controller.RunCommandAsync("bell decision on");
        Assert.Equal("bell decision on", context.Output.Texts[^1].Trim());
        Assert.Equal(OwnerConsoleBellTiers.All, context.Settings.Current.BellTiers);
        Assert.False(context.Saved.ContainsKey("mutedTiers"));
        Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
    }

    [Fact]
    public async Task SoundList_ListsDecisionLastWithIndependentSelection()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        await context.Controller.RunCommandAsync("sound set decision 1");
        await context.Controller.RunCommandAsync("sound list");
        var lines = context.Output.Texts[^1].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(["calm: old.wav", "attention: system sound", "alarm: system sound", "decision: first.wav"],
            lines.Where(line => !line.StartsWith(' ')));
        Assert.Equal("  [1] first.wav", lines[^2]);
        Assert.Equal("  [2] two words.wav", lines[^1]);
    }

    [Fact]
    public void AlertsTab_DecisionLast_SupportsMutePreviewSelectAndSystem()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var view = new OwnerConsoleSettingsView([tab]);
        view.Open(OwnerConsolePane.Board);
        Assert.Equal(["calm: old.wav (on)", "attention: system sound (on)",
                "alarm: system sound (on)", "decision: system sound (on)"],
            view.Lines.Where(line => line.StartsWith("calm:", StringComparison.Ordinal) ||
                line.StartsWith("attention:", StringComparison.Ordinal) ||
                line.StartsWith("alarm:", StringComparison.Ordinal) || line.StartsWith("decision:", StringComparison.Ordinal)));
        OwnerConsoleSettingsTestContext.Highlight(view, "decision:");
        view.HandleKey(new Key('m'));
        Assert.Equal(new(true, true, true, false), context.Settings.Current.BellTiers);
        Assert.Contains("decision: system sound (muted)", view.Lines);
        Assert.Equal(["decision"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
        view.HandleKey(new Key('m'));
        Assert.Equal(OwnerConsoleBellTiers.All, context.Settings.Current.BellTiers);
        var bytes = File.ReadAllBytes(context.Path);
        var snapshot = context.Settings.Current;
        view.HandleKey(Key.CursorDown);
        view.HandleKey(Key.CursorDown);
        view.HandleKey(new Key('p'));
        Assert.Equal([context.Clip("decision")], context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
        Assert.Same(snapshot, context.Settings.Current);
        view.HandleKey(Key.Enter);
        Assert.Equal(context.Clip("decision"), context.Settings.Current.DecisionSoundFile);
        Assert.Equal(context.Clip("decision"), context.Saved["decisionSoundFile"]!.GetValue<string>());
        Assert.Null(context.Settings.Current.AlarmSoundFile);
        Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
        view.HandleKey(new Key('s'));
        Assert.Null(context.Settings.Current.DecisionSoundFile);
        Assert.False(context.Saved.ContainsKey("decisionSoundFile"));
        Assert.Null(context.Settings.Current.AlarmSoundFile);
    }
}
