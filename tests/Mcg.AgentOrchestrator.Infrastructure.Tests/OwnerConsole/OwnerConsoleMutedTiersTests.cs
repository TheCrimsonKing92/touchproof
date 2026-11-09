using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: isolated settings, controller, fake audio and injected time per case.
public sealed class OwnerConsoleMutedTiersTests
{
    [Fact]
    public async Task TabMute_PersistsAcrossRestart_AndBellUpdatesOpenTab()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        using (var view = new OwnerConsoleSettingsView([new OwnerConsoleAlertsSettingsTab(context.Sound)]))
        {
            view.Open(OwnerConsolePane.Board);
            OwnerConsoleSettingsTestContext.Highlight(view, "alarm:");
            view.HandleKey(new Key('m'));
            Assert.Equal(["alarm"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
            Assert.Equal("calm on, attention on, alarm off", context.Controller.BellStatusText);
            Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
        }

        context.Restart();
        Assert.Equal("calm on, attention on, alarm off", context.Controller.BellStatusText);
        context.Controller.Apply(OwnerConsoleSettingsTestContext.Model(running: true));
        context.Controller.Apply(OwnerConsoleSettingsTestContext.Model());
        context.Clock.Now += context.Settings.Current.UnplannedStopGrace + TimeSpan.FromSeconds(1);
        context.Controller.Apply(OwnerConsoleSettingsTestContext.Model());
        Assert.Empty(context.Audio.Files);
        Assert.Empty(context.Audio.SystemSounds);
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        using var restarted = new OwnerConsoleSettingsView([tab]);
        restarted.Open(OwnerConsolePane.Activity);
        Assert.Contains(restarted.Lines, line => line == "alarm: system sound (muted)");
        await context.Controller.RunCommandAsync("bell attention off");
        Assert.Equal("bell attention off", context.Output.Texts[^1].Trim());
        Assert.Equal("calm on, attention off, alarm off", context.Controller.BellStatusText);
        Assert.Contains(restarted.Lines, line => line == "attention: system sound (muted)");
        Assert.Equal(["attention", "alarm"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
        var expected = context.Original.DeepClone().AsObject();
        expected["mutedTiers"] = new JsonArray("attention", "alarm");
        Assert.True(JsonNode.DeepEquals(expected, context.Saved));

        await context.Controller.RunCommandAsync("bell on");
        Assert.Equal("on", context.Controller.BellStatusText);
        Assert.DoesNotContain(restarted.Lines, line => line.Contains("(muted)"));
        Assert.False(context.Saved.ContainsKey("mutedTiers"));
        await context.Controller.RunCommandAsync("bell off");
        Assert.Equal("off", context.Controller.BellStatusText);
        Assert.Equal(["calm", "attention", "alarm"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
        await context.Controller.RunCommandAsync("bell alarm on");
        Assert.Contains(restarted.Lines, line => line == "alarm: system sound (on)");
        Assert.Equal(["calm", "attention"], context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Theory]
    [InlineData("null", "on")]
    [InlineData("{}", "on")]
    [InlineData("\"alarm\"", "on")]
    [InlineData("[1,null,\"unknown\",\"calm\",\"calm\",\"alarm\"]", "calm off, attention on, alarm off")]
    public void MuteLoader_IgnoresMalformedAndUnknownEntries(string muted, string expected)
    {
        using var context = new OwnerConsoleSettingsTestContext(muted);
        Assert.Equal(expected, context.Controller.BellStatusText);
        Assert.Equal(context.Settings.Current, OwnerConsoleNotificationSettings.Load(context.Path));
    }

    [Fact]
    public async Task FailedBellWrite_LeavesRuntimeAndTabUnchanged()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        File.Delete(context.Path);
        Directory.CreateDirectory(context.Path);
        var snapshot = context.Settings.Current;
        await context.Controller.RunCommandAsync("bell alarm off");
        Assert.StartsWith("bell save failed:", context.Output.Texts[^1]);
        Assert.Same(snapshot, context.Settings.Current);
        Assert.Equal("on", context.Controller.BellStatusText);
        var tab = new OwnerConsoleAlertsSettingsTab(context.Sound);
        Assert.DoesNotContain(tab.Lines, line => line.Contains("(muted)"));
        Assert.Empty(Directory.GetFiles(context.Root));
    }
}
