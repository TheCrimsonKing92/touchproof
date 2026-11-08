using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: every test owns its directory, settings, audio output and clock.
public sealed class OwnerConsoleSoundSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetAndClear_ControllerDispatch_PreservesKeysAndChangesNextAlert(bool exactDuplicateKey)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var library = Path.Combine(root, "library");
            var folder = Directory.CreateDirectory(Path.Combine(library, "calm")).FullName;
            File.WriteAllText(Path.Combine(folder, "first.wav"), "");
            var second = Path.Combine(folder, "second.wav");
            File.WriteAllText(second, "");
            var document = new JsonObject
            {
                ["attentionThresholdMinutes"] = 7, ["staleTickThresholdMinutes"] = 3,
                ["calmCoalesceSeconds"] = 1, ["attentionSoundFile"] = "attention.wav",
                ["alarmSoundFile"] = "alarm.wav", ["soundLibrary"] = "library",
                ["CalmSoundFile"] = "old.wav", ["calmSoundFile"] = "shadow.wav",
                ["unknown"] = new JsonObject { ["nested"] = new JsonArray(1, true, "value") }
            };
            File.WriteAllText(path, document.ToJsonString());
            var provider = new OwnerConsoleNotificationSettingsProvider(OwnerConsoleNotificationSettings.Load(path));
            var audio = new AudioOutput();
            var notifier = new SystemOwnerConsoleNotifier(audio, provider.Current, File.Exists, current: () => provider.Current);
            var command = new OwnerConsoleSoundCommand(path, provider, notifier, audio, Path.Combine(root, "unused"),
                Directory.Exists, Directory.GetFiles);
            var harness = new OwnerConsoleHarness();
            var dialogs = new Dialogs();
            var clock = new ManualClock();
            using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
                harness.State, harness.Tail, harness.Conductor, harness.DigestReport,
                harness.Digest, clock, notifier: notifier, notifications: provider.Current, sound: command);
            controller.Apply(Model(0));
            Assert.Empty(audio.Files);
            Assert.Empty(audio.SystemSounds);

            // A key added after startup must survive the command's fresh read, too.
            document["addedAfterStartup"] = new JsonArray("keep", 2);
            var text = document.ToJsonString();
            if (exactDuplicateKey) text = "{\"attentionThresholdMinutes\":99," + text[1..];
            File.WriteAllText(path, text);
            using (var input = JsonDocument.Parse(File.ReadAllText(path)))
                Assert.Equal(exactDuplicateKey ? 2 : 1, input.RootElement.EnumerateObject()
                    .Count(field => field.Name == "attentionThresholdMinutes"));
            Assert.Equal(TimeSpan.FromMinutes(7), OwnerConsoleNotificationSettings.Load(path).AttentionThreshold);
            await controller.RunCommandAsync("sound set calm 2");
            Assert.Equal("sound set calm: second.wav", dialogs.Texts[^1].Trim());
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(Path.GetFullPath(second), saved["calmSoundFile"]!.GetValue<string>());
            Assert.False(saved.ContainsKey("CalmSoundFile"));
            AssertOtherKeys(document, saved);
            Assert.Equal(second, provider.Current.CalmSoundFile);
            Assert.Empty(audio.Files); // Selecting never previews implicitly.
            Assert.Empty(audio.SystemSounds);
            controller.Apply(Model(1));
            Assert.Equal([second], audio.Files);
            Assert.Empty(audio.SystemSounds);

            await controller.RunCommandAsync("bell off");
            audio.Files.Clear();
            var bytes = File.ReadAllBytes(path);
            await controller.RunCommandAsync("sound play calm");
            Assert.Equal([second], audio.Files);
            Assert.False(controller.BellEnabled);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            await controller.RunCommandAsync("bell on");

            await controller.RunCommandAsync("sound set calm system");
            saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.False(saved.ContainsKey("calmSoundFile"));
            AssertOtherKeys(document, saved);
            Assert.Null(provider.Current.CalmSoundFile);
            audio.Files.Clear();
            clock.Now += TimeSpan.FromMinutes(2);
            controller.Apply(Model(2));
            Assert.Empty(audio.Files);
            Assert.Equal([0x40u], audio.SystemSounds);
            Assert.Equal([path], Directory.GetFiles(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Set_UnavailableDocument_WritesOnlyChangedKey(string? text)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "new-settings", "settings.json");
            if (text is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            var provider = new OwnerConsoleNotificationSettingsProvider(new());
            var clip = Path.Combine(root, "calm", "only.wav");
            var command = new OwnerConsoleSoundCommand(path, provider, new NullOwnerConsoleNotifier(), new AudioOutput(),
                root, _ => true, _ => [clip]);
            var output = new OwnerConsoleCapturedOutput();
            command.Run("sound set calm 1", output);
            Assert.Equal("sound set calm: only.wav", output.Text.Trim());
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("calmSoundFile", Assert.Single(saved).Key);
            Assert.Equal(clip, saved["calmSoundFile"]!.GetValue<string>());
            Assert.Equal(clip, provider.Current.CalmSoundFile);
            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Clear_MissingLibraryAndSettings_CreatesEmptyObject()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var provider = new OwnerConsoleNotificationSettingsProvider(new() { CalmSoundFile = "old.wav" });
            var command = new OwnerConsoleSoundCommand(path, provider, new NullOwnerConsoleNotifier(), new AudioOutput(),
                root, _ => throw new InvalidOperationException("clear must not inspect the library"),
                _ => throw new InvalidOperationException("clear must not enumerate"));
            var output = new OwnerConsoleCapturedOutput();
            command.Run("sound set calm system", output);
            Assert.Equal("sound set calm: system sound", output.Text.Trim());
            Assert.Empty(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
            Assert.Null(provider.Current.CalmSoundFile);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void SaveFailure_PreservesActiveSelectionAndRemovesTemporaryFile()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            // A directory at the destination deterministically refuses atomic publication.
            Directory.CreateDirectory(path);
            var provider = new OwnerConsoleNotificationSettingsProvider(new() { CalmSoundFile = "old.wav" });
            var snapshot = provider.Current;
            var command = new OwnerConsoleSoundCommand(path, provider, new NullOwnerConsoleNotifier(), new AudioOutput(),
                root, _ => false, _ => []);
            var output = new OwnerConsoleCapturedOutput();
            command.Run("sound set calm system", output);
            Assert.StartsWith("sound set failed:", output.Text);
            Assert.Same(snapshot, provider.Current);
            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("calm")]
    [InlineData("attention")]
    [InlineData("alarm")]
    public void Set_Filename_PersistsEachTierAndPublishesItsSelection(string tier)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            var clip = Path.Combine(root, tier, "two words.WAV");
            var provider = new OwnerConsoleNotificationSettingsProvider(new());
            var command = new OwnerConsoleSoundCommand(path, provider, new NullOwnerConsoleNotifier(), new AudioOutput(),
                root, _ => true, _ => [clip]);
            var output = new OwnerConsoleCapturedOutput();
            command.Run($"sound set {tier} \"two words.wav\"", output);
            Assert.Equal($"sound set {tier}: two words.WAV", output.Text.Trim());
            Assert.Equal(clip, JsonNode.Parse(File.ReadAllText(path))![tier + "SoundFile"]!.GetValue<string>());
            Assert.Equal(OwnerConsoleNotificationSettings.Load(path), provider.Current);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void AssertOtherKeys(JsonObject original, JsonObject saved)
    {
        var expected = original.DeepClone().AsObject();
        expected.Remove("CalmSoundFile");
        expected.Remove("calmSoundFile");
        var actual = saved.DeepClone().AsObject();
        actual.Remove("calmSoundFile");
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }
    private static OwnerConsoleViewModel Model(int landed) => new(new(false, 0, 0, 0, null, landed), [], [], []);
    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AudioOutput : IOwnerConsoleSoundOutput
    {
        internal readonly List<string> Files = [];
        internal readonly List<uint> SystemSounds = [];
        public bool TryPlayFile(string path) { Files.Add(path); return true; }
        public void PlaySystemSound(uint messageBeepType) => SystemSounds.Add(messageBeepType);
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }
}
