using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: sound and file existence use local recording fakes; no native calls.
public sealed class SystemOwnerConsoleNotifierTests
{
    [Fact]
    public void DefaultMappingUsesTheThreeSystemAliases()
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new(), _ => throw new InvalidOperationException("no file configured"));
        notifier.Notify(OwnerConsoleNotificationTier.Calm);
        notifier.Notify(OwnerConsoleNotificationTier.Attention);
        notifier.Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Equal([0x40u, 0x30u, 0x10u], output.SystemSounds);
        Assert.Empty(output.Files);
    }

    [Theory]
    [InlineData(0, 0x40u)]
    [InlineData(1, 0x30u)]
    [InlineData(2, 0x10u)]
    public void ExistingFileOverridesSoundAndFailureFallsBack(int tierValue, uint alias)
    {
        var tier = (OwnerConsoleNotificationTier)tierValue;
        var output = new SoundOutput();
        var settings = new OwnerConsoleNotificationSettings
        { CalmSoundFile = "custom.wav", AttentionSoundFile = "custom.wav", AlarmSoundFile = "custom.wav" };
        var checkedPaths = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(output, settings, path => { checkedPaths.Add(path); return true; });
        notifier.Notify(tier);
        Assert.Equal(["custom.wav"], checkedPaths);
        Assert.Equal(["custom.wav"], output.Files);
        Assert.Empty(output.SystemSounds);
        output.Success = false;
        notifier.Notify(tier);
        Assert.Equal(["custom.wav", "custom.wav"], output.Files);
        Assert.Equal([alias], output.SystemSounds);
    }

    [Fact]
    public void MissingFileUsesSystemSoundWithoutTryingTheFile()
    {
        var output = new SoundOutput();
        var checkedPaths = new List<string>();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { AlarmSoundFile = "missing.wav" },
            path => { checkedPaths.Add(path); return false; });
        notifier.Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Equal(["missing.wav"], checkedPaths);
        Assert.Empty(output.Files);
        Assert.Equal([0x10u], output.SystemSounds);
    }

    [Fact]
    public void FileCheckFailureFallsBackAndNullNotifierHasNoOutput()
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "custom.wav" },
            _ => throw new IOException("file unavailable"));
        notifier.Notify(OwnerConsoleNotificationTier.Calm);
        Assert.Equal([0x40u], output.SystemSounds);
        Assert.Empty(output.Files);
        output.SystemSounds.Clear();
        new NullOwnerConsoleNotifier().Notify(OwnerConsoleNotificationTier.Alarm);
        Assert.Empty(output.SystemSounds);
        Assert.Empty(output.Files);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FolderChoice_UsesRandomIndexAndExcludesPreviousClip(int tierValue)
    {
        var output = new SoundOutput();
        var bounds = new List<int>();
        var listedPaths = new List<string>();
        var settings = new OwnerConsoleNotificationSettings
        { CalmSoundFile = "clips", AttentionSoundFile = "clips", AlarmSoundFile = "clips" };
        var notifier = new SystemOwnerConsoleNotifier(output, settings, _ => false, _ => true,
            path => { listedPaths.Add(path); return ["b.wav", "a.wav", "c.wav"]; },
            count => { bounds.Add(count); return bounds.Count == 1 ? 1 : 0; });

        notifier.Notify((OwnerConsoleNotificationTier)tierValue);
        notifier.Notify((OwnerConsoleNotificationTier)tierValue);

        Assert.Equal(["b.wav", "a.wav"], output.Files);
        Assert.Equal([3, 2], bounds);
        Assert.Equal(["clips", "clips"], listedPaths);
        Assert.Empty(output.SystemSounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderWithoutWavs_UsesSystemSound(bool containsOtherFiles)
    {
        var output = new SoundOutput();
        var choices = 0;
        var notifier = new SystemOwnerConsoleNotifier(output, new() { AlarmSoundFile = "clips" },
            _ => false, _ => true, _ => containsOtherFiles ? ["notes.txt", "audio.mp3"] : [],
            _ => { choices++; return 0; });

        notifier.Notify(OwnerConsoleNotificationTier.Alarm);

        Assert.Empty(output.Files);
        Assert.Equal([0x10u], output.SystemSounds);
        Assert.Equal(0, choices);
    }

    [Fact]
    public void FailedFolderPick_FallsBackWithoutRetryOrChangingHistory()
    {
        var output = new SoundOutput();
        var bounds = new List<int>();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { AttentionSoundFile = "clips" },
            _ => false, _ => true, _ => ["a.wav", "b.wav", "c.wav"],
            count => { bounds.Add(count); return bounds.Count == 3 ? 1 : 0; });

        notifier.Notify(OwnerConsoleNotificationTier.Attention);
        output.Success = false;
        notifier.Notify(OwnerConsoleNotificationTier.Attention);
        Assert.Equal(["a.wav", "b.wav"], output.Files);
        Assert.Equal([0x30u], output.SystemSounds);
        output.Success = true;
        notifier.Notify(OwnerConsoleNotificationTier.Attention);

        Assert.Equal(["a.wav", "b.wav", "c.wav"], output.Files);
        Assert.Equal([3, 2, 2], bounds);
        Assert.Equal([0x30u], output.SystemSounds);
    }

    [Fact]
    public void SingleWavFolder_PlaysTheSameClipEveryTime()
    {
        var output = new SoundOutput();
        var bounds = new List<int>();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "clips" },
            _ => false, _ => true, _ => ["only.WAV", "notes.txt"],
            count => { bounds.Add(count); return 0; });

        for (var i = 0; i < 3; i++) notifier.Notify(OwnerConsoleNotificationTier.Calm);

        Assert.Equal(["only.WAV", "only.WAV", "only.WAV"], output.Files);
        Assert.Equal([1, 1, 1], bounds);
        Assert.Empty(output.SystemSounds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileSetting_NeverConsultsFolderDependencies(bool playbackSucceeds)
    {
        var output = new SoundOutput { Success = playbackSucceeds };
        var folderChecks = 0;
        var listings = 0;
        var choices = 0;
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "custom.wav" },
            _ => true, _ => { folderChecks++; return true; },
            _ => { listings++; return ["other.wav"]; }, _ => { choices++; return 0; });

        notifier.Notify(OwnerConsoleNotificationTier.Calm);

        Assert.Equal(["custom.wav"], output.Files);
        Assert.Equal(playbackSucceeds ? [] : new[] { 0x40u }, output.SystemSounds);
        Assert.Equal(0, folderChecks);
        Assert.Equal(0, listings);
        Assert.Equal(0, choices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FolderAccessFailure_UsesSystemSound(int failurePoint)
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "clips" },
            _ => false, _ => failurePoint == 0 ? throw new UnauthorizedAccessException() : true,
            _ => failurePoint == 1 ? throw new IOException() : FailingListing(), _ => 0);

        notifier.Notify(OwnerConsoleNotificationTier.Calm);

        Assert.Empty(output.Files);
        Assert.Equal([0x40u], output.SystemSounds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidChoice_UsesSystemSound(int choice)
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { AlarmSoundFile = "clips" },
            _ => false, _ => true, _ => ["a.wav", "b.wav"], _ => choice);

        notifier.Notify(OwnerConsoleNotificationTier.Alarm);

        Assert.Empty(output.Files);
        Assert.Equal([0x10u], output.SystemSounds);
    }

    [Fact]
    public void FolderHistory_IsIndependentForEachTier()
    {
        var output = new SoundOutput();
        var notifier = new SystemOwnerConsoleNotifier(output,
            new() { CalmSoundFile = "clips", AttentionSoundFile = "clips" },
            _ => false, _ => true, _ => ["a.wav", "b.wav"], _ => 0);

        for (var i = 0; i < 2; i++)
        {
            notifier.Notify(OwnerConsoleNotificationTier.Calm);
            notifier.Notify(OwnerConsoleNotificationTier.Attention);
        }

        Assert.Equal(["a.wav", "a.wav", "b.wav", "b.wav"], output.Files);
        Assert.Empty(output.SystemSounds);
    }

    [Fact]
    public void EquivalentPaths_AreDeduplicatedBeforeExcludingPreviousClip()
    {
        var output = new SoundOutput();
        var bounds = new List<int>();
        var notifier = new SystemOwnerConsoleNotifier(output, new() { CalmSoundFile = "clips" },
            _ => false, _ => true, _ => ["a.wav", Path.Combine(".", "A.WAV"), "b.wav"],
            count => { bounds.Add(count); return 0; });

        notifier.Notify(OwnerConsoleNotificationTier.Calm);
        notifier.Notify(OwnerConsoleNotificationTier.Calm);

        Assert.Equal(["a.wav", "b.wav"], output.Files);
        Assert.Equal([2, 1], bounds);
        Assert.Empty(output.SystemSounds);
    }

    private static IEnumerable<string> FailingListing()
    {
        yield return "a.wav";
        throw new IOException("listing unavailable");
    }

    private sealed class SoundOutput : IOwnerConsoleSoundOutput
    {
        internal readonly List<uint> SystemSounds = [];
        internal readonly List<string> Files = [];
        internal bool Success = true;
        public void PlaySystemSound(uint messageBeepType) => SystemSounds.Add(messageBeepType);
        public bool TryPlayFile(string path) { Files.Add(path); return Success; }
    }
}
