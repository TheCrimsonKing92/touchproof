using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory and input text.
public sealed class AcceptanceLaunchLockSummaryBySiteTests : IDisposable
{
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void Preserve_SiteLines_CarriesMatchingPidAndKeepsExistingBytes()
    {
        const string summary = "LAUNCH_LOCK_SUMMARY pid=4242 launches=2";
        const string byClass = "LAUNCH_LOCK_BY_CLASS pid=4242 class=A launches=2 create_ms_total=1.000 by_entry=GitCli:2";
        const string site = "LAUNCH_LOCK_BY_SITE pid=4242 class=A site=Helper.Run git=status launches=2";
        var existing = $"LAUNCH_LOCK_SUMMARY pid=7 launches=1\ndiagnostic\n{summary}\n{byClass}\n";
        var additional = "LAUNCH_LOCK_BY_SITE pid=7 class=Nested site=Other.Run git=- launches=1\n" + site + "\n" +
            "LAUNCH_LOCK_BY_SITE pid=42420 class=Other site=Other.Run git=- launches=1\n " + site;
        var attempt = Path.Combine(root, "attempt");
        var trx = Path.Combine(attempt, "lane.trx");
        var prefix = Path.Combine(attempt, "acceptance");
        var path = Path.Combine(attempt, "lane.launch-lock-summary.txt");
        AcceptanceLaunchLockSummary.Preserve("unused", existing, trx, prefix);
        var original = File.ReadAllText(path);
        Assert.Equal(string.Join(Environment.NewLine, summary, byClass, ""), original);
        AcceptanceLaunchLockSummary.Preserve("unused", existing + additional, trx, prefix);
        Assert.Equal(original + site + Environment.NewLine, File.ReadAllText(path));
        AcceptanceLaunchLockSummary.Preserve(existing + additional, null, trx, prefix);
        Assert.Equal(original + site + Environment.NewLine, File.ReadAllText(path));
        AcceptanceLaunchLockSummary.Preserve("unused", existing, trx, prefix);
        Assert.Equal(original, File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
