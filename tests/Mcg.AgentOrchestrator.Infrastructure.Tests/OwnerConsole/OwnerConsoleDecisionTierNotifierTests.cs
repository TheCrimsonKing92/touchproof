using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: injected filesystem, chooser and recording audio; no native playback.
public sealed class OwnerConsoleDecisionTierNotifierTests
{
    [Fact]
    public void ConfiguredDecisionClip_PlaysOnce_WithoutReadingAlarmOrLibrary()
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput();
        var checks = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(audio,
            new() { DecisionSoundFile = "decision.wav", AlarmSoundFile = "alarm.wav" },
            path => { checks.Add(path); return true; },
            decisionLibraryFolder: _ => throw new InvalidOperationException("configured clip owns playback"));

        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Equal(["decision.wav"], checks);
        Assert.Equal(["decision.wav"], audio.Files);
        Assert.Empty(audio.SystemSounds);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed-play")]
    [InlineData("check-error")]
    public void UnusableDecisionClip_FallsBackToAttentionAliasOnce(string failure)
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput { PlaySucceeds = failure != "failed-play" };
        var checks = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(audio, new() { DecisionSoundFile = "decision.wav" },
            path =>
            {
                checks.Add(path);
                return failure == "check-error" ? throw new IOException("unreadable") : failure == "failed-play";
            });

        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Equal(["decision.wav"], checks);
        Assert.Equal(failure == "failed-play" ? new[] { "decision.wav" } : [], audio.Files);
        Assert.Equal([0x30u], audio.SystemSounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsetDecision_UsesSharedLibraryFolderAndRotatesClips(bool relativeRoot)
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput();
        var root = Path.Combine(Path.GetTempPath(), "decision-notifier-" + Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(root, "settings.json");
        var defaultRoot = Path.Combine(root, "default-sounds");
        var settings = new OwnerConsoleNotificationSettings
        { SoundLibrary = relativeRoot ? Path.Combine("relative", "sounds") : null };
        var expectedFolder = Path.Combine(relativeRoot ? Path.Combine(root, "relative", "sounds") : defaultRoot, "decision");
        var folderChecks = new List<string>();
        var listings = new List<string>();
        var bounds = new List<int>();
        var notifier = new SystemOwnerConsoleNotifier(audio, settings, _ => false,
            folder => { folderChecks.Add(folder); return folder == expectedFolder; },
            folder => { listings.Add(folder); return [Path.Combine(folder, "a.wav"), Path.Combine(folder, "b.WAV"), Path.Combine(folder, "ignore.mp3")]; },
            count => { bounds.Add(count); return 0; },
            decisionLibraryFolder: snapshot => OwnerConsoleSoundCommand.LibraryFolder(
                settingsPath, snapshot.SoundLibrary, defaultRoot, OwnerConsoleNotificationTier.Decision));

        notifier.Notify(OwnerConsoleNotificationTier.Decision);
        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Equal([expectedFolder, expectedFolder], folderChecks);
        Assert.Equal([expectedFolder, expectedFolder], listings);
        Assert.Equal([Path.Combine(expectedFolder, "a.wav"), Path.Combine(expectedFolder, "b.WAV")], audio.Files);
        Assert.Equal([2, 1], bounds);
        Assert.Empty(audio.SystemSounds);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("listing-error")]
    [InlineData("resolver-error")]
    [InlineData("bad-choice")]
    public void UnavailableDefaultLibrary_FallsBackOnce(string failure)
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput();
        var resolutions = 0;
        var notifier = new SystemOwnerConsoleNotifier(audio, new(), _ => false,
            _ => failure != "missing",
            _ => failure == "listing-error" ? throw new IOException("listing refused") :
                failure == "empty" ? [] : ["clip.wav"],
            _ => failure == "bad-choice" ? -1 : 0,
            decisionLibraryFolder: _ =>
            {
                resolutions++;
                return failure == "resolver-error" ? throw new IOException("resolution refused") : "clips";
            });

        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Equal(1, resolutions);
        Assert.Empty(audio.Files);
        Assert.Equal([0x30u], audio.SystemSounds);
    }

    [Fact]
    public void UnsetDecision_WithoutResolver_UsesSystemFallback()
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput();
        var notifier = new SystemOwnerConsoleNotifier(audio, new(),
            _ => throw new InvalidOperationException("no path should be checked"));

        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Empty(audio.Files);
        Assert.Equal([0x30u], audio.SystemSounds);
    }

    [Fact]
    public void PublishedDecisionSetting_IsUsedOnNextNotification()
    {
        var audio = new OwnerConsoleSettingsTestContext.AudioOutput();
        var provider = new OwnerConsoleNotificationSettingsProvider(new() { DecisionSoundFile = "before.wav" });
        var notifier = new SystemOwnerConsoleNotifier(audio, provider.Current, _ => true,
            current: () => provider.Current);
        notifier.Notify(OwnerConsoleNotificationTier.Decision);
        provider.Publish(provider.Current with { DecisionSoundFile = "after.wav" });

        notifier.Notify(OwnerConsoleNotificationTier.Decision);

        Assert.Equal(["before.wav", "after.wav"], audio.Files);
        Assert.Empty(audio.SystemSounds);
    }
}
