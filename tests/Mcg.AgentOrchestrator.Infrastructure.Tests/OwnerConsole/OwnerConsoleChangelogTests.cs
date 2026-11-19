using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: parser fixtures are in memory.
public sealed class OwnerConsoleChangelogTests
{
    private const string Fingerprint = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void UnseenEntriesRespectTheRecordedIdAndChangelogOrder()
    {
        var markdown = $"# Console changelog\r\n\r\nAppend entries below.\r\n\r\n{Entry(1)}\r\n{Entry(3)}\r\n{Entry(7)}";
        var entries = OwnerConsoleChangelog.Parse(markdown);
        Assert.NotNull(entries);

        Assert.Collection(OwnerConsoleChangelog.Unseen(entries, 1),
            entry =>
            {
                Assert.Equal(3, entry.Id);
                Assert.Equal("Change 3", entry.Summary);
                Assert.Equal(Fingerprint, entry.Fingerprint);
                Assert.Equal("Type :settings for 3.", Assert.Single(entry.Navigation));
            },
            entry => Assert.Equal(7, entry.Id));
        Assert.Empty(OwnerConsoleChangelog.Unseen(entries, 7));
        Assert.Empty(OwnerConsoleChangelog.Unseen(entries, 8));
    }

    [Theory]
    [InlineData("## 2 - Missing fingerprint\n- Type :settings.")]
    [InlineData("## 2 - Invalid fingerprint\nSurface fingerprint: sha256:wrong\n- Type :settings.")]
    [InlineData("## 2 - Missing navigation\nSurface fingerprint: " + Fingerprint)]
    [InlineData("## 1 - Duplicate id\nSurface fingerprint: " + Fingerprint + "\n- Type :settings.")]
    [InlineData("## 0 - Invalid id\nSurface fingerprint: " + Fingerprint + "\n- Type :settings.")]
    public void MalformedEntryRejectsTheEntireChangelog(string invalidEntry)
    {
        var first = Entry(1);
        Assert.NotNull(OwnerConsoleChangelog.Parse(first));
        Assert.Null(OwnerConsoleChangelog.Parse($"{first}\n\n{invalidEntry}"));
    }

    private static string Entry(int id) =>
        $"## {id} - Change {id}\nSurface fingerprint: {Fingerprint}\n- Type :settings for {id}.";
}
