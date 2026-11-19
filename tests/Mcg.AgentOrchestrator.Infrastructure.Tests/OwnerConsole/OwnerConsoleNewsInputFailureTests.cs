using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each case owns its input files and notice strip.
public sealed class OwnerConsoleNewsInputFailureTests
{
    private const string ValidChangelog = """
        ## 1 - New settings tab
        Surface fingerprint: sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        - Type :settings.
        """;
    private const string MalformedChangelog = "## 1 - New settings tab\n- Type :settings.";

    [Theory]
    [InlineData(null, "0")]
    [InlineData(MalformedChangelog, "0")]
    [InlineData(ValidChangelog, "{")]
    [InlineData(ValidChangelog, "-1")]
    [InlineData(null, null)]
    public async Task StartupWithMissingOrMalformedInputCompletesWithoutANotice(string? changelog, string? record)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var changelogPath = Path.Combine(root, OwnerConsoleChangelog.FileName);
            var recordPath = Path.Combine(root, OwnerConsoleNewsRecord.FileName);
            if (changelog is not null) File.WriteAllText(changelogPath, changelog);
            if (record is not null) File.WriteAllText(recordPath, record);
            var strip = new OwnerConsoleNoticeStrip(new OwnerConsoleTestClock());

            var session = OwnerConsoleNewsRecord.Start(changelogPath, recordPath,
                text => strip.Raise(text, OwnerConsoleNoticeSeverity.Announcement, OwnerConsoleNoticeSource.Announcement),
                strip.DismissAnnouncement,
                text => strip.Raise(text, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action));
            await TestHangGuard.WaitAsync(session.Loaded, "startup news load with missing or malformed input");

            Assert.Empty(strip.Visible);
            Assert.Empty(session.Unseen);
            Assert.Equal(record, File.Exists(recordPath) ? File.ReadAllText(recordPath) : null);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
