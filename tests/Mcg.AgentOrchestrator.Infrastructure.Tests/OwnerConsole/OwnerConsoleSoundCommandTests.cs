using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: unique settings directory, injected library and recording audio only.
public sealed class OwnerConsoleSoundCommandTests
{
    [Theory]
    [InlineData("sound")]
    [InlineData(" :SoUnD LiSt ")]
    public void List_AllTiers_UsesStableNumbersAndSelectedBasenames(string line)
    {
        using var fixture = new Fixture();
        var output = fixture.Run(line);
        Assert.Equal(new[]
        {
            "calm: chosen.wav", "  [1] alpha.wav", "  [2] Beta.WAV",
            "attention: system sound", "  (no clips)",
            "alarm: system sound", $"alarm library folder not found: {Path.Combine(fixture.Root, "alarm") }"
        }, output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Empty(fixture.Audio.Files);
        Assert.Empty(fixture.Audio.SystemSounds);
    }

    [Fact]
    public void Preview_SelectedAndExplicitClips_PlayOnceWithoutChangingSettings()
    {
        using var fixture = new Fixture();
        var bytes = File.ReadAllBytes(fixture.SettingsPath);
        var original = fixture.Settings.Current;
        fixture.Run("sound play calm");
        Assert.Equal([original.CalmSoundFile!], fixture.Audio.Files);
        fixture.Audio.Files.Clear();
        fixture.Run("sound play CALM 2");
        Assert.Equal([Path.Combine(fixture.Root, "calm", "Beta.WAV")], fixture.Audio.Files);
        Assert.Empty(fixture.Audio.SystemSounds);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Same(original, fixture.Settings.Current);
    }

    [Theory]
    [InlineData("sound play calm beta.wav", "Beta.WAV")]
    [InlineData("sound play calm \"two words.wav\"", "two words.wav")]
    [InlineData("sound play calm two words.wav", "two words.wav")]
    [InlineData("sound play calm 'two words.wav'", "two words.wav")]
    public void Preview_FilenameSelectors_MatchWholeNameIgnoringCase(string line, string expected)
    {
        using var fixture = new Fixture();
        fixture.Clips.Add(Path.Combine(fixture.Root, "calm", "two words.wav"));
        fixture.Run(line);
        Assert.Equal([Path.Combine(fixture.Root, "calm", expected)], fixture.Audio.Files);
        Assert.Empty(fixture.Audio.SystemSounds);
    }

    [Fact]
    public void Preview_Failures_OnlySelectedSoundFallsBack()
    {
        using var fixture = new Fixture();
        fixture.Audio.Success = false;
        fixture.Run("sound play calm");
        Assert.Equal([0x40u], fixture.Audio.SystemSounds);
        fixture.Audio.SystemSounds.Clear();
        Assert.Contains("could not play", fixture.Run("sound play calm 1"));
        Assert.Empty(fixture.Audio.SystemSounds);
        fixture.Run("sound play alarm"); // A selected sound does not need a library folder.
        Assert.Equal([0x10u], fixture.Audio.SystemSounds);
    }

    [Theory]
    [InlineData("sound set loud 1", false)]
    [InlineData("sound set calm 9", false)]
    [InlineData("sound set calm nope.wav", false)]
    [InlineData("sound set calm 1", true)]
    [InlineData("sound play loud 1", false)]
    [InlineData("sound play calm 9", false)]
    [InlineData("sound play calm nope.wav", false)]
    [InlineData("sound play calm 1", true)]
    [InlineData("sound set calm ../alpha.wav", false)]
    [InlineData("sound list extra", false)]
    [InlineData("sound set calm", false)]
    [InlineData("sound set calm 0", false)]
    public void InvalidSelection_RefusesWithoutWritingOrPlaying(string line, bool missingFolder)
    {
        using var fixture = new Fixture();
        fixture.MissingCalm = missingFolder;
        var bytes = File.ReadAllBytes(fixture.SettingsPath);
        var snapshot = fixture.Settings.Current;
        var output = fixture.Run(line);
        Assert.True(output.StartsWith("usage: sound", StringComparison.Ordinal) || output.Contains("not found"), output);
        Assert.Equal(bytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Same(snapshot, fixture.Settings.Current);
        Assert.Empty(fixture.Audio.Files);
        Assert.Empty(fixture.Audio.SystemSounds);
        Assert.Equal([fixture.SettingsPath], Directory.GetFiles(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = SharedTestSupport.CreateTempDirectory();
        internal string SettingsPath => Path.Combine(Root, "settings.json");
        internal readonly OwnerConsoleNotificationSettingsProvider Settings;
        internal readonly AudioOutput Audio = new();
        internal readonly List<string> Clips;
        internal bool MissingCalm;
        private readonly OwnerConsoleSoundCommand _command;

        internal Fixture()
        {
            File.WriteAllText(SettingsPath, "{\n  \"unknown\": [1, true]\n}\n");
            Settings = new(new() { CalmSoundFile = Path.Combine(Root, "external", "chosen.wav") });
            Clips = [Path.Combine(Root, "calm", "Beta.WAV"), Path.Combine(Root, "calm", "alpha.wav"),
                Path.Combine(Root, "calm", "notes.txt"), Path.Combine(Root, "calm", "nested", "ignored.wav")];
            var notifier = new SystemOwnerConsoleNotifier(Audio, Settings.Current, _ => true, current: () => Settings.Current);
            _command = new(SettingsPath, Settings, notifier, Audio, Root,
                folder => folder != Path.Combine(Root, "alarm") && (!MissingCalm || folder != Path.Combine(Root, "calm")),
                folder => folder == Path.Combine(Root, "calm") ? Clips : []);
        }

        internal string Run(string line)
        {
            var output = new OwnerConsoleCapturedOutput();
            _command.Run(line, output);
            return output.Text;
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }

    private sealed class AudioOutput : IOwnerConsoleSoundOutput
    {
        internal readonly List<string> Files = [];
        internal readonly List<uint> SystemSounds = [];
        internal bool Success = true;
        public bool TryPlayFile(string path) { Files.Add(path); return Success; }
        public void PlaySystemSound(uint messageBeepType) => SystemSounds.Add(messageBeepType);
    }
}
