using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Each consumer owns its directory and injected IO, audio and time; no global GUI state.
internal sealed class OwnerConsoleSettingsTestContext : IDisposable
{
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
    internal string Path { get; }
    internal string Library { get; }
    internal OwnerConsoleNotificationSettingsProvider Settings { get; private set; } = null!;
    internal OwnerConsoleSoundCommand Sound { get; private set; } = null!;
    internal SystemOwnerConsoleNotifier Notifier { get; private set; } = null!;
    internal OwnerConsoleScreenController Controller { get; private set; } = null!;
    internal OwnerConsoleTestClock Clock { get; } = new();
    internal AudioOutput Audio { get; } = new();
    internal Dialogs Output { get; } = new();
    internal Func<string, IEnumerable<string>> Listing { get; set; }
    internal bool FolderExists { get; set; } = true;
    internal JsonObject Original { get; }

    internal OwnerConsoleSettingsTestContext(string muted = "[]")
    {
        Path = System.IO.Path.Combine(Root, "settings.json");
        Library = System.IO.Path.Combine(Root, "library");
        Original = new()
        {
            ["soundLibrary"] = Library, ["calmSoundFile"] = "old.wav",
            ["attentionThresholdMinutes"] = 30, ["staleTickThresholdMinutes"] = 15,
            ["calmCoalesceSeconds"] = 60,
            ["unknown"] = new JsonObject { ["nested"] = new JsonArray(1, true, "keep") },
            ["mutedTiers"] = JsonNode.Parse(muted)
        };
        File.WriteAllText(Path, Original.ToJsonString());
        Listing = folder => [System.IO.Path.Combine(folder, "two words.wav"),
            System.IO.Path.Combine(folder, "first.wav"), System.IO.Path.Combine(folder, "ignore.mp3")];
        Restart();
    }

    internal void Restart()
    {
        Controller?.Dispose();
        Settings = new(OwnerConsoleNotificationSettings.Load(Path));
        var provider = Settings;
        Notifier = new(Audio, provider.Current, _ => true, current: () => provider.Current);
        Sound = new(Path, Settings, Notifier, Audio, System.IO.Path.Combine(Root, "unused"),
            _ => FolderExists, folder => Listing(folder));
        var harness = new OwnerConsoleHarness();
        Controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, Output,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, Clock,
            notifier: Notifier, notifications: Settings.Current, sound: Sound);
    }

    internal string Clip(string tier) => System.IO.Path.Combine(Library, tier, "two words.wav");
    internal JsonObject Saved => JsonNode.Parse(File.ReadAllText(Path))!.AsObject();

    internal static void Highlight(OwnerConsoleSettingsView view, string startsWith)
    {
        // Home and a bounded row walk exercise exactly the same navigation as the console.
        view.HandleKey(Key.Home);
        var target = view.Lines.Select((line, index) => (line, index))
            .First(pair => pair.line.StartsWith(startsWith, StringComparison.Ordinal)).index;
        for (var index = 0; index < target; index++) view.HandleKey(Key.CursorDown);
    }

    internal static void Edit(OwnerConsoleSettingsView view, string key, string value)
    {
        Highlight(view, key + ":");
        view.HandleKey(Key.Enter);
        foreach (var character in value) view.HandleKey(new Key(character));
        view.HandleKey(Key.Enter);
    }

    internal static OwnerConsoleViewModel Model(int landed = 0, bool running = false) =>
        new(new(running, 0, 0, 0, null, landed), [], [], []);

    internal static OwnerConsoleDecision Decision(DateTimeOffset raised) =>
        new("question", 1, "goal", "goal", OwnerQuestionKind.HumanInput, "Answer?", "Answer?", null, null, null, raised);

    public void Dispose()
    {
        Controller.Dispose();
        SharedTestSupport.RemoveTempDirectory(Root);
    }

    internal sealed class AudioOutput : IOwnerConsoleSoundOutput
    {
        internal readonly List<string> Files = [];
        internal readonly List<uint> SystemSounds = [];
        internal bool PlaySucceeds = true;
        public bool TryPlayFile(string path) { Files.Add(path); return PlaySucceeds; }
        public void PlaySystemSound(uint messageBeepType) => SystemSounds.Add(messageBeepType);
    }

    internal sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) { Texts.Add(text); return Task.CompletedTask; }
    }
}
