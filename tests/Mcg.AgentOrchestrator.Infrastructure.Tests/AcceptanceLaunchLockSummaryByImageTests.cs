using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory and input text.
public sealed class AcceptanceLaunchLockSummaryByImageTests : IDisposable
{
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact]
    public void Preserve_ImageLines_KeepsOnlyLastSummaryPidInInputOrder()
    {
        const string summary = "LAUNCH_LOCK_SUMMARY pid=4242 launches=2";
        const string byClass = "LAUNCH_LOCK_BY_CLASS pid=4242 class=A launches=2 create_ms_total=1.000 by_entry=GitCli:2";
        const string image = "LAUNCH_LOCK_BY_IMAGE pid=4242 class=A git=1 powershell=0 pwsh=0 icacls=1 cmd=0 dotnet=0 other=0";
        var input = string.Join('\n', "LAUNCH_LOCK_SUMMARY pid=7 launches=1", summary, byClass,
            image.Replace("pid=4242", "pid=7"), image, image.Replace("pid=4242", "pid=42420"),
            " " + image, image.Replace("pid=4242 ", ""), image.Replace("pid=4242", "pid=bad"));
        var attempt = Path.Combine(root, "attempt");
        var trx = Path.Combine(attempt, "lane.trx");
        var prefix = Path.Combine(attempt, "acceptance");
        var path = Path.Combine(attempt, "lane.launch-lock-summary.txt");
        AcceptanceLaunchLockSummary.Preserve("unused", input, trx, prefix);
        Assert.Equal(string.Join(Environment.NewLine, summary, byClass, image, ""), File.ReadAllText(path));
        AcceptanceLaunchLockSummary.Preserve(input, null, trx, prefix);
        Assert.Equal(string.Join(Environment.NewLine, summary, byClass, image, ""), File.ReadAllText(path));
        AcceptanceLaunchLockSummary.Preserve(image, null, trx, prefix);
        Assert.False(File.Exists(path));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
