using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its record, clock, notice strip, and dialogs.
public sealed class OwnerConsoleNewsAnnouncementTests : IDisposable
{
    private const string Summary = "Find Provider connections in Settings.";
    private const string Navigation = "Type :settings and press Left/Right to select Provider connections.";
    private const string MarkdownNavigation = "Type `:settings` and press Left/Right to select Provider connections.";
    private const string SecondSummary = "Read recent changes in Console news.";
    private const string SecondNavigation = "Type :news to see the details.";
    private const string SecondMarkdownNavigation = "Type `:news` to see the details.";
    private const string Fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private string ChangelogPath => Path.Combine(_root, OwnerConsoleChangelog.FileName);
    private string RecordPath => Path.Combine(_root, OwnerConsoleNewsRecord.FileName);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupWithoutRecordAnnouncesBothEntriesWithoutWriting(bool missingRecordDirectory)
    {
        var recordDirectory = missingRecordDirectory ? Path.Combine(_root, "workspace") : _root;
        var recordPath = Path.Combine(recordDirectory, OwnerConsoleNewsRecord.FileName);
        var strip = new OwnerConsoleNoticeStrip(new OwnerConsoleTestClock());
        WriteFirstRunChangelog();
        var session = StartSession(strip, recordPath);
        await TestHangGuard.WaitAsync(session.Loaded, "first-run news load");

        var announcement = Assert.Single(strip.Visible);
        Assert.Equal(OwnerConsoleNoticeSource.Announcement, announcement.Source);
        Assert.Equal($"Console news (:news): {Summary} {Navigation} | {SecondSummary} {SecondNavigation}",
            announcement.Text);
        Assert.Equal(new[] { 1, 2 }, session.Unseen.Select(entry => entry.Id));
        Assert.False(File.Exists(recordPath));
        Assert.Equal(!missingRecordDirectory, Directory.Exists(recordDirectory));
    }

    [Fact]
    public async Task FirstRunNewsShowsBothEntriesAndAcknowledgesThemAcrossRestart()
    {
        var clock = new OwnerConsoleTestClock();
        var strip = new OwnerConsoleNoticeStrip(clock);
        WriteFirstRunChangelog();
        var session = StartSession(strip);
        await TestHangGuard.WaitAsync(session.Loaded, "first-run news load before acknowledgement");

        var dialogs = new NewsDialogs();
        using var controller = CreateController(session, dialogs, clock);
        await TestHangGuard.WaitAsync(controller.RunCommandAsync(":news"), "first-run news acknowledgement");

        var expectedDetails =
            $"1 - {Summary}\n- {Navigation}\n\n" +
            $"2 - {SecondSummary}\n- {SecondNavigation}";
        Assert.Equal(("Console news", expectedDetails), Assert.Single(dialogs.Shown));
        var marked = Assert.IsType<OwnerConsoleMarkedText>(dialogs.Marked);
        Assert.Equal(new Range(7, 16), Assert.Single(marked.KeysAt(1)));
        Assert.Equal(new Range(7, 12), Assert.Single(marked.KeysAt(4)));
        Assert.Equal("2", File.ReadAllText(RecordPath));
        Assert.Empty(strip.Visible);

        var restartedStrip = new OwnerConsoleNoticeStrip(clock);
        var restartedSession = StartSession(restartedStrip);
        await TestHangGuard.WaitAsync(restartedSession.Loaded, "first-run news load after acknowledgement and restart");
        Assert.Empty(restartedStrip.Visible);
        Assert.Empty(restartedSession.Unseen);
    }

    [Fact]
    public async Task UnseenNewsSurvivesSuccessExpiryAndStaysDismissedAfterRestart()
    {
        var clock = new OwnerConsoleTestClock();
        var strip = new OwnerConsoleNoticeStrip(clock);
        var session = await StartWithUnseenNewsAsync(strip);

        var announcement = Assert.Single(strip.Visible);
        Assert.Equal(OwnerConsoleNoticeSource.Announcement, announcement.Source);
        Assert.Equal($"Console news (:news): {Summary} {Navigation}", announcement.Text);
        Assert.Equal("1", File.ReadAllText(RecordPath));

        strip.Raise("ordinary success", OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action);
        Assert.Equal(2, strip.Visible.Count);
        clock.Now += OwnerConsoleNoticeStrip.SuccessLifetime + TimeSpan.FromSeconds(1);
        Assert.Equal(announcement, Assert.Single(strip.Visible));

        var dialogs = new NewsDialogs();
        using var controller = CreateController(session, dialogs, clock);
        await TestHangGuard.WaitAsync(controller.RunCommandAsync(":news"), "news acknowledgement");
        Assert.Equal(("Console news", $"3 - {Summary}\n- {Navigation}"),
            Assert.Single(dialogs.Shown));
        Assert.Empty(strip.Visible);
        Assert.Equal("3", File.ReadAllText(RecordPath));

        var restartedStrip = new OwnerConsoleNoticeStrip(clock);
        var restartedSession = StartSession(restartedStrip);
        await TestHangGuard.WaitAsync(restartedSession.Loaded, "news load after restart");
        Assert.Empty(restartedStrip.Visible);
        Assert.Empty(restartedSession.Unseen);
    }

    [Fact]
    public async Task FailedAcknowledgementRetainsNewsForRetry()
    {
        var clock = new OwnerConsoleTestClock();
        var strip = new OwnerConsoleNoticeStrip(clock);
        var session = await StartWithUnseenNewsAsync(strip);
        var announcement = Assert.Single(strip.Visible);
        var dialogs = new NewsDialogs();
        using var controller = CreateController(session, dialogs, clock);

        // An existing directory at the final path prevents atomic replacement.
        var savedRecord = Path.Combine(_root, "saved-record.json");
        File.Move(RecordPath, savedRecord);
        Directory.CreateDirectory(RecordPath);
        await TestHangGuard.WaitAsync(controller.RunCommandAsync(":news"), "failed news acknowledgement");

        Assert.Contains(announcement, strip.Visible);
        var failure = Assert.Single(strip.Visible, entry => entry.Severity == OwnerConsoleNoticeSeverity.Failure);
        Assert.Contains("Run :news to retry.", failure.Text);
        Assert.Equal(3, Assert.Single(session.Unseen).Id);
        Assert.Equal("1", File.ReadAllText(savedRecord));

        Directory.Delete(RecordPath);
        File.Move(savedRecord, RecordPath);
        await TestHangGuard.WaitAsync(controller.RunCommandAsync(":news"), "retried news acknowledgement");

        Assert.Equal(2, dialogs.Shown.Count);
        Assert.Equal(dialogs.Shown[0], dialogs.Shown[1]);
        Assert.DoesNotContain(announcement, strip.Visible);
        Assert.Empty(session.Unseen);
        Assert.Equal("3", File.ReadAllText(RecordPath));
    }

    private async Task<OwnerConsoleNewsRecord> StartWithUnseenNewsAsync(OwnerConsoleNoticeStrip strip)
    {
        File.WriteAllText(ChangelogPath,
            $"## 3 - {Summary}\nSurface fingerprint: {Fingerprint}\n- {MarkdownNavigation}");
        File.WriteAllText(RecordPath, "1");
        var session = StartSession(strip);
        await TestHangGuard.WaitAsync(session.Loaded, "startup news load");
        return session;
    }

    private void WriteFirstRunChangelog() =>
        File.WriteAllText(ChangelogPath,
            $"## 1 - {Summary}\nSurface fingerprint: {Fingerprint}\n- {MarkdownNavigation}\n\n" +
            $"## 2 - {SecondSummary}\nSurface fingerprint: {Fingerprint}\n- {SecondMarkdownNavigation}");

    private OwnerConsoleNewsRecord StartSession(OwnerConsoleNoticeStrip strip, string? recordPath = null) =>
        OwnerConsoleNewsRecord.Start(ChangelogPath, recordPath ?? RecordPath,
            text => strip.Raise(text, OwnerConsoleNoticeSeverity.Announcement, OwnerConsoleNoticeSource.Announcement),
            strip.DismissAnnouncement,
            text => strip.Raise(text, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action));

    private static OwnerConsoleScreenController CreateController(
        OwnerConsoleNewsRecord session, NewsDialogs dialogs, TimeProvider clock)
    {
        var harness = new OwnerConsoleHarness(clock);
        var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock);
        controller.News = session;
        return controller;
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);

    private sealed class NewsDialogs : IOwnerConsoleDialogs
    {
        internal List<(string Title, string Text)> Shown { get; } = [];
        internal OwnerConsoleMarkedText? Marked { get; private set; }

        public Task<bool> ConfirmAsync(string title, string text) =>
            throw new InvalidOperationException("News opened a confirmation dialog.");

        public Task<string?> PromptTextAsync(string title, string text) =>
            throw new InvalidOperationException("News opened an input dialog.");

        public Task ShowTextAsync(string title, string text)
        {
            Shown.Add((title, text));
            return Task.CompletedTask;
        }

        public Task ShowMarkedTextAsync(string title, OwnerConsoleMarkedText text)
        {
            Marked = text;
            return ShowTextAsync(title, text.PlainText);
        }
    }
}
