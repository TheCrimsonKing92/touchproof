using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its directory and all input text.
public sealed class AcceptanceLaunchLockSummaryByClassTests : IDisposable
{
    private const string Summary = "LAUNCH_LOCK_SUMMARY pid=4242 launches=2";
    private const string ClassLine = "LAUNCH_LOCK_BY_CLASS pid=4242 class=A launches=2 create_ms_total=1.000 by_entry=GitCli:2";
    private readonly string root = InfrastructureTestSupport.CreateTempDirectory();
    private string AttemptDirectory => Path.Combine(root, "attempt");
    private string TrxPath => Path.Combine(AttemptDirectory, "lane.trx");
    private string AttemptPrefix => Path.Combine(AttemptDirectory, "acceptance");
    private string SummaryPath => Path.Combine(AttemptDirectory, "lane.launch-lock-summary.txt");

    [Fact]
    public void Preserve_LastSummaryCarriesOnlyMatchingPidLinesInInputOrder()
    {
        const string nested = "LAUNCH_LOCK_BY_CLASS pid=7 class=Nested launches=1";
        const string second = "LAUNCH_LOCK_BY_CLASS pid=4242 class=B launches=1";
        var stderr = $"LAUNCH_LOCK_SUMMARY pid=7 launches=1\n{nested}\ndiagnostic\n{Summary}\n{ClassLine}\n{second}\n" +
            "LAUNCH_LOCK_BY_CLASS pid=42420 class=Other launches=1\n LAUNCH_LOCK_BY_CLASS pid=4242 class=Indented";
        AcceptanceLaunchLockSummary.Preserve("unused", stderr, TrxPath, AttemptPrefix);
        Assert.Equal(string.Join(Environment.NewLine, Summary, ClassLine, second, ""), File.ReadAllText(SummaryPath));
        Assert.Equal(SummaryPath, Assert.Single(Directory.GetFiles(AttemptDirectory)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Preserve_AbsentStderrCarriesOutputClasses(string? stderr)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary + "\n" + ClassLine, stderr, TrxPath, AttemptPrefix);
        Assert.Equal(Summary + Environment.NewLine + ClassLine + Environment.NewLine, File.ReadAllText(SummaryPath));
    }

    [Theory]
    [InlineData("diagnostic only")]
    [InlineData(" \t")]
    public void Preserve_NonEmptyStderrStillWinsOverOutput(string stderr)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary + "\n" + ClassLine, stderr, TrxPath, AttemptPrefix);
        Assert.False(Directory.Exists(AttemptDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Preserve_InvalidAttemptPrefixCreatesNothing(string? prefix)
    {
        AcceptanceLaunchLockSummary.Preserve(Summary + "\n" + ClassLine, Summary + "\n" + ClassLine, TrxPath, prefix);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Theory]
    [InlineData("diagnostic only")]
    [InlineData(" LAUNCH_LOCK_SUMMARY pid=4242 launches=2")]
    [InlineData("diagnostic LAUNCH_LOCK_SUMMARY pid=4242 launches=2")]
    public void Preserve_ClassesWithoutStartingSummaryCreateNothing(string output)
    {
        AcceptanceLaunchLockSummary.Preserve(output + "\n" + ClassLine, null, TrxPath, AttemptPrefix);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void Preserve_ClassesWithoutSummaryRemoveStaleFile()
    {
        Directory.CreateDirectory(AttemptDirectory);
        File.WriteAllText(SummaryPath, "stale");
        Assert.True(File.Exists(SummaryPath));
        AcceptanceLaunchLockSummary.Preserve(ClassLine, null, TrxPath, AttemptPrefix);
        Assert.False(File.Exists(SummaryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(AttemptDirectory));
    }

    [Theory]
    [InlineData("LAUNCH_LOCK_SUMMARY launches=2")]
    [InlineData("LAUNCH_LOCK_SUMMARY pid=invalid launches=2")]
    [InlineData("LAUNCH_LOCK_SUMMARY pid=4242 launches=2")]
    public void Preserve_NoMatchingClassLinesKeepsExactSummary(string summary)
    {
        AcceptanceLaunchLockSummary.Preserve(summary + "\nLAUNCH_LOCK_BY_CLASS pid=7 class=Nested", null, TrxPath, AttemptPrefix);
        Assert.Equal(summary + Environment.NewLine, File.ReadAllText(SummaryPath));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
