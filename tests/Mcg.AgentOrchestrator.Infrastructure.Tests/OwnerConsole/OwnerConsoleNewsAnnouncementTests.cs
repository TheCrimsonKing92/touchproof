using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each test owns its record, clock, notice strip, and dialogs.
public sealed class OwnerConsoleNewsAnnouncementTests : IDisposable
{
    private const string Summary = "Find Provider connections in Settings.";
    private const string Navigation = "Type :settings and press Left/Right to select Provider connections.";
    private const string Fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private string ChangelogPath => Path.Combine(_root, OwnerConsoleChangelog.FileName);
    private string RecordPath => Path.Combine(_root, OwnerConsoleNewsRecord.FileName);

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
        Assert.Equal(("Console news", $"3 - {Summary}\nSurface fingerprint: {Fingerprint}\n- {Navigation}"),
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
        File.WriteAllText(ChangelogPath, $"## 3 - {Summary}\nSurface fingerprint: {Fingerprint}\n- {Navigation}");
        File.WriteAllText(RecordPath, "1");
        var session = StartSession(strip);
        await TestHangGuard.WaitAsync(session.Loaded, "startup news load");
        return session;
    }

    private OwnerConsoleNewsRecord StartSession(OwnerConsoleNoticeStrip strip) =>
        OwnerConsoleNewsRecord.Start(ChangelogPath, RecordPath,
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

        public Task<bool> ConfirmAsync(string title, string text) =>
            throw new InvalidOperationException("News opened a confirmation dialog.");

        public Task<string?> PromptTextAsync(string title, string text) =>
            throw new InvalidOperationException("News opened an input dialog.");

        public Task ShowTextAsync(string title, string text)
        {
            Shown.Add((title, text));
            return Task.CompletedTask;
        }
    }
}
