using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its dialogs, notifier, model, clock and settings directory.
public sealed class OwnerConsoleBellTierCommandTests
{
    [Theory]
    [InlineData("bell off", "bell off")]
    [InlineData("bell on", "bell on")]
    [InlineData("bell calm off", "bell calm off")]
    [InlineData(" :BeLl CaLm ON ", "bell calm on")]
    public async Task SuccessfulBellDispatchShowsOneNoticeAndNoDialog(string command, string expected)
    {
        var dialogs = new Dialogs();
        using var controller = Controller(new OwnerConsoleHarness(), dialogs, new ManualClock());
        var notices = new List<string>();
        controller.CommandNotice = notices.Add;

        await controller.RunCommandAsync(command);

        Assert.Equal(expected, Assert.Single(notices));
        Assert.Empty(dialogs.Texts);
        Assert.Empty(dialogs.Titles);
    }

    [Theory]
    [InlineData("1", "first.wav")]
    [InlineData("system", "system sound")]
    public async Task SuccessfulSoundSetDispatchShowsOneNoticeAndNoDialog(string selector, string expected)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var notices = new List<string>();
        context.Controller.CommandNotice = notices.Add;

        await context.Controller.RunCommandAsync("sound set decision " + selector);

        Assert.Equal("sound set decision: " + expected, Assert.Single(notices));
        Assert.Empty(context.Output.Texts);
    }

    [Theory]
    [InlineData("bell", false, "usage: bell")]
    [InlineData("bell loud on", false, "usage: bell")]
    [InlineData("bell alarm off", true, "bell save failed:")]
    [InlineData("sound set decision missing.wav", false, "clip not found")]
    [InlineData("sound set decision 1", true, "sound set failed:")]
    [InlineData("sound list", false, "calm")]
    [InlineData("nonsense", false, "unknown command")]
    [InlineData("digest", false, "Owner digest: landed=2 pending=1")]
    public async Task NonAcknowledgementDispatchKeepsResultDialog(string command, bool failSave, string expected)
    {
        using var context = new OwnerConsoleSettingsTestContext();
        var dialogs = new Dialogs();
        using var controller = Controller(new OwnerConsoleHarness(), dialogs, context.Clock,
            sound: context.Sound, summary: new StructuredDigest());
        var notices = new List<string>();
        controller.CommandNotice = notices.Add;
        await controller.RunCommandAsync("bell off");
        Assert.Equal("bell off", Assert.Single(notices));
        Assert.Empty(dialogs.Texts);
        Assert.Empty(dialogs.Titles);
        notices.Clear();

        if (failSave)
        {
            File.Delete(context.Path);
            Directory.CreateDirectory(context.Path);
            Assert.True(Directory.Exists(context.Path));
        }
        await controller.RunCommandAsync(command);

        Assert.Empty(notices);
        Assert.Equal("Command result", Assert.Single(dialogs.Titles));
        Assert.Contains(expected, Assert.Single(dialogs.Texts));
        Assert.Equal(command == "digest" ? 1 : 0, dialogs.StructuredCount);
    }

    [Fact]
    public async Task AcknowledgementDoesNotCarryIntoTheNextCommand()
    {
        var dialogs = new Dialogs();
        using var controller = Controller(new OwnerConsoleHarness(), dialogs, new ManualClock());
        var notices = new List<string>();
        controller.CommandNotice = notices.Add;

        await controller.RunCommandAsync("bell off");
        await controller.RunCommandAsync("bell");

        Assert.Equal("bell off", Assert.Single(notices));
        Assert.Equal("Command result", Assert.Single(dialogs.Titles));
        Assert.StartsWith("usage: bell", Assert.Single(dialogs.Texts));
    }

    [Fact]
    public async Task NoticeSinkCanBeAttachedAfterDispatchWasCreated()
    {
        var dialogs = new Dialogs();
        using var controller = Controller(new OwnerConsoleHarness(), dialogs, new ManualClock());
        await controller.RunCommandAsync("bell off");
        Assert.Equal("bell off", Assert.Single(dialogs.Texts).Trim());
        var notices = new List<string>();
        controller.CommandNotice = notices.Add;

        await controller.RunCommandAsync("bell on");

        Assert.Equal("bell on", Assert.Single(notices));
        Assert.Single(dialogs.Texts);
        Assert.Single(dialogs.Titles);
    }

    [Theory]
    [InlineData(" acknowledgement ", " acknowledgement ")]
    [InlineData("acknowledgement\n", "acknowledgement")]
    [InlineData("acknowledgement\r", "acknowledgement")]
    [InlineData("acknowledgement\r\n", "acknowledgement")]
    [InlineData(" acknowledgement \r\n", " acknowledgement ")]
    public void SingleLineAcknowledgementRemovesOnlyOneTerminator(string text, string expected)
    {
        var output = new OwnerConsoleCapturedOutput();
        output.Write(text);

        Assert.True(output.TryAcknowledgementLine(out var line));
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("acknowledgement\n\n")]
    [InlineData("acknowledgement\r\n\r\n")]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    public void EmptyOrMultilineOutputIsNotAnAcknowledgementLine(string text)
    {
        var output = new OwnerConsoleCapturedOutput();
        output.Write(text);

        Assert.False(output.TryAcknowledgementLine(out _));
    }

    [Fact]
    public async Task AllAndPerTierCommandsControlPlaybackAndCanonicalOutput()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new ManualClock();
        var dialogs = new Dialogs();
        var notifier = new RecordingNotifier();
        using var controller = Controller(harness, dialogs, clock, notifier);
        var model = Model(3);
        controller.Apply(model);
        await controller.RunCommandAsync("bell off");
        Assert.False(controller.BellEnabled);
        Assert.Equal("off", controller.BellStatusText);
        Assert.Equal("bell off", dialogs.Texts[^1].Trim());
        model = Model(4);
        controller.Apply(model);
        Assert.Empty(notifier.Calls);
        await controller.RunCommandAsync("bell on");
        Assert.True(controller.BellEnabled);
        Assert.Equal("on", controller.BellStatusText);
        Assert.Equal("bell on", dialogs.Texts[^1].Trim());
        controller.Apply(model);
        Assert.Empty(notifier.Calls);

        await controller.RunCommandAsync("bell attention off");
        Assert.True(controller.BellEnabled);
        Assert.Equal("calm on, attention off, alarm on, decision on", controller.BellStatusText);
        Assert.Equal("bell attention off", dialogs.Texts[^1].Trim());
        var question = new OwnerConsoleDecision("q", 1, "goal", "goal", OwnerQuestionKind.HumanInput,
            "Answer?", "Answer?", null, null, null, clock.Now.AddMinutes(-29));
        model = model with { Decisions = [question] };
        controller.Apply(model);
        clock.Now = clock.Now.AddMinutes(2);
        controller.Apply(model);
        Assert.Equal([OwnerConsoleNotificationTier.Decision], notifier.Calls);
        controller.Apply(model with { Status = model.Status with { LandedToday = 5 } });
        Assert.Equal([OwnerConsoleNotificationTier.Decision, OwnerConsoleNotificationTier.Calm], notifier.Calls);
        await controller.RunCommandAsync("bell attention on");
        controller.Apply(controller.Model!);
        Assert.Equal([OwnerConsoleNotificationTier.Decision, OwnerConsoleNotificationTier.Calm], notifier.Calls);

        await controller.RunCommandAsync("bell alarm off");
        Assert.Equal("calm on, attention on, alarm off, decision on", controller.BellStatusText);
        await controller.RunCommandAsync("bell alarm on");
        Assert.Equal("on", controller.BellStatusText);
        await controller.RunCommandAsync("bell calm off");
        Assert.Equal("calm off, attention on, alarm on, decision on", controller.BellStatusText);
        await controller.RunCommandAsync(" :BeLl CaLm ON ");
        Assert.Equal("on", controller.BellStatusText);
        Assert.Equal("bell calm on", dialogs.Texts[^1].Trim());
    }

    [Theory]
    [InlineData("bell loud on")]
    [InlineData("bell calm maybe")]
    [InlineData("bell")]
    [InlineData("bell calm")]
    [InlineData("bell on extra")]
    public async Task InvalidCommandPrintsUsageAndKeepsState(string command)
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, new ManualClock());
        await controller.RunCommandAsync(command);
        Assert.Equal("usage: bell on|off | bell <calm|attention|alarm|decision> on|off", Assert.Single(dialogs.Texts).Trim());
        Assert.Equal("on", controller.BellStatusText);
    }

    [Fact]
    public void MissingOrThrowingNotifierDoesNotBreakModelApplication()
    {
        var harness = new OwnerConsoleHarness();
        using var silent = Controller(harness, new Dialogs(), new ManualClock());
        silent.Apply(Model(0));
        var landed = Model(1);
        silent.Apply(landed);
        Assert.Same(landed, silent.Model);
        var broken = new RecordingNotifier { Throw = true };
        using var controller = Controller(harness, new Dialogs(), new ManualClock(), broken);
        controller.Apply(Model(0));
        controller.Apply(landed);
        Assert.Equal([OwnerConsoleNotificationTier.Calm], broken.Calls);
        Assert.Same(landed, controller.Model);
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness, Dialogs dialogs,
        TimeProvider clock, IOwnerConsoleNotifier? notifier = null, OwnerConsoleSoundCommand? sound = null,
        IOwnerDigestSummary? summary = null) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers,
        dialogs, harness.State, harness.Tail, harness.Conductor, harness.DigestReport, summary ?? harness.Digest, clock,
        notifier: notifier, sound: sound);
    private static OwnerConsoleViewModel Model(int landed) => new(new(ConductorLiveness.NotRunning, 0, 0, 0, null, landed), [], [], []);

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class RecordingNotifier : IOwnerConsoleNotifier
    {
        internal readonly List<OwnerConsoleNotificationTier> Calls = [];
        internal bool Throw;
        public void Notify(OwnerConsoleNotificationTier tier)
        {
            Calls.Add(tier);
            if (Throw) throw new IOException("sound failed");
        }
    }
    private sealed class StructuredDigest : IOwnerDigestSummary
    {
        public IReadOnlyList<string> ReadSummaryLines() => ["Owner digest: landed=2 pending=1"];
        public (IReadOnlyList<string> Lines, OwnerConsoleStructuredText? Fields) ReadStructuredSummary() =>
            (ReadSummaryLines(), new(OwnerConsoleFieldSnapshot.Create("digest", new OwnerConsoleField("landed", "Landed", "2"))));
    }
    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<string> Texts = [];
        internal readonly List<string> Titles = [];
        internal int StructuredCount;
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) { Titles.Add(title); Texts.Add(text); return Task.CompletedTask; }
        public async Task<int?> ShowStructuredAsync(string title, string text, OwnerConsoleStructuredText fields,
            IReadOnlyList<int>? choiceLines = null)
        {
            StructuredCount++;
            await ShowTextAsync(title, text);
            return null;
        }
    }
}
