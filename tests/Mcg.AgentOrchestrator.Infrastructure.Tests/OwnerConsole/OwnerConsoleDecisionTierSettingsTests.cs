using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: settings files live in uniquely owned temporary directories.
public sealed class OwnerConsoleDecisionTierSettingsTests
{
    [Fact]
    public void Load_DecisionClipAndMute_RoundTripWithoutRewriting()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var document = context.Original.DeepClone().AsObject();
        document["DECISIONSOUNDFILE"] = "decision.wav";
        document["mutedTiers"] = new System.Text.Json.Nodes.JsonArray("decision");
        File.WriteAllText(context.Path, document.ToJsonString());
        var bytes = File.ReadAllBytes(context.Path);

        var loaded = OwnerConsoleNotificationSettings.Load(context.Path);

        Assert.Equal("decision.wav", loaded.DecisionSoundFile);
        Assert.Equal(new(true, true, true, false), loaded.BellTiers);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"mutedTiers\":[\"calm\",\"attention\",\"alarm\"]}")]
    [InlineData("{\"decisionSoundFile\":\" \"}")]
    [InlineData("{\"decisionSoundFile\":4,\"mutedTiers\":[null,1,\"unknown\"]}")]
    public void Load_OlderOrInvalidDecisionSettings_EnablesDecisionWithoutWriting(string text)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        File.WriteAllText(context.Path, text);
        var bytes = File.ReadAllBytes(context.Path);

        var loaded = OwnerConsoleNotificationSettings.Load(context.Path);

        Assert.True(loaded.BellTiers.Decision);
        Assert.Null(loaded.DecisionSoundFile);
        Assert.Equal(bytes, File.ReadAllBytes(context.Path));
    }

    [Fact]
    public void BellStates_AllNoneAndOmittedFlag_IncludeDecisionConsistently()
    {
        Assert.True(OwnerConsoleBellTiers.All.Decision);
        Assert.False(OwnerConsoleBellTiers.None.Decision);
        Assert.True(new OwnerConsoleBellTiers(false, true, false).Decision);
        Assert.True(OwnerConsoleBellTiers.All.IsOn(OwnerConsoleNotificationTier.Decision));
        Assert.False(OwnerConsoleBellTiers.None.IsOn(OwnerConsoleNotificationTier.Decision));
        Assert.Equal(new(true, true, true, false),
            OwnerConsoleBellTiers.All.With(OwnerConsoleNotificationTier.Decision, false));
        Assert.Equal(new(false, false, false, true),
            OwnerConsoleBellTiers.None.With(OwnerConsoleNotificationTier.Decision, true));
    }

    [Fact]
    public void Persist_AllOff_WritesDecisionLastAndAllOnClearsMutedArray()
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var output = new OwnerConsoleCapturedOutput();

        Assert.True(context.Sound.SetBellTiers(OwnerConsoleBellTiers.None, output));

        Assert.Equal(["calm", "attention", "alarm", "decision"],
            context.Saved["mutedTiers"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal(OwnerConsoleBellTiers.None, OwnerConsoleNotificationSettings.Load(context.Path).BellTiers);
        Assert.True(context.Sound.SetBellTiers(OwnerConsoleBellTiers.All, output));
        Assert.False(context.Saved.ContainsKey("mutedTiers"));
        Assert.Equal(OwnerConsoleBellTiers.All, OwnerConsoleNotificationSettings.Load(context.Path).BellTiers);
        Assert.False(context.Saved.ContainsKey("decisionSoundFile"));
    }
}
