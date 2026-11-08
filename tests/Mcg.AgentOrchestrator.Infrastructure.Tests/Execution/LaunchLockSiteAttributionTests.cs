using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private telemetry except one real git launch attributed to this test class.
public sealed class LaunchLockSiteAttributionTests
{
    [Fact]
    public void GitLaunch_ConfigPairs_ReportsHelperAndSubcommand()
    {
        var attribution = LaunchLockTelemetry.Attribution;
        Assert.True(attribution.IsActive);
        var key = typeof(LaunchLockSiteAttributionTests).FullName!;
        var site = new LaunchLockSite("LaunchLockSiteAttributionTests.RunGit", "version");
        var before = attribution.SiteSnapshot().Where(row => row.Key == key && row.Site == site)
            .Sum(row => row.Launches);
        var root = Path.Combine(Path.GetTempPath(), "launch-site-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = RunGit(root);
            Assert.True(result.ProcessStarted, result.Error);
            Assert.Equal(0, result.ExitCode);
            var row = Assert.Single(attribution.SiteSnapshot().Where(row => row.Key == key && row.Site == site));
            Assert.Equal(before + 1, row.Launches);
            Assert.Contains($"LAUNCH_LOCK_BY_SITE pid=42 class={key} site={site.Site} git=version launches={before + 1}",
                LaunchLockSiteFormatter.FormatBySite(attribution.Snapshot().Where(row => row.Key == key).ToArray(),
                    attribution.SiteSnapshot(), 42));
            Assert.DoesNotContain(attribution.SiteSnapshot().Where(row => row.Key == key), row =>
                row.Site.Site.StartsWith("GitCli.", StringComparison.Ordinal) ||
                row.Site.Site.StartsWith("HandleList", StringComparison.Ordinal) ||
                row.Site.Site.StartsWith("LaunchLock", StringComparison.Ordinal) &&
                !row.Site.Site.StartsWith("LaunchLockSiteAttributionTests.", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("status", "-c", "a=b", "status")]
    [InlineData("log", "-c", "a=b", "-c", "x=y", "--no-pager", "log", "-1")]
    [InlineData("-")]
    [InlineData("-", "-c")]
    [InlineData("-", "-c", "a=b")]
    [InlineData("-", "--version")]
    public void GitArguments_ConfigPairs_SelectsFirstNonOption(string expected, params string[] args)
        => Assert.Equal(expected, LaunchLockSiteCapture.GitSubcommand(args));

    [Fact]
    public async Task RecordInto_StateMachines_ReportsSourceMethods()
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        await RecordAsync(attribution);
        Assert.Single(RecordIterator(attribution));
        Assert.Equal(new[]
        {
            new LaunchLockSite("LaunchLockSiteAttributionTests.RecordAsync", "-"),
            new LaunchLockSite("LaunchLockSiteAttributionTests.RecordIterator", "-")
        }, attribution.SiteSnapshot().Select(row => row.Site).OrderBy(row => row.Site, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RecordInto_NoKey_NeverCapturesAndPreservesTeardownBytes()
    {
        var walks = 0;
        var attribution = new LaunchLockAttribution(_ =>
        {
            walks++;
            return new("Unexpected.Walk", "status");
        });
        var counters = new LaunchLockCounters();
        using (LaunchLockSiteCapture.BeginGit(["status"]))
        {
            LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.GitCli, 2, 3, 4);
            attribution.SetProvider(() => null);
            LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.GitCli, 2, 3, 4);
            attribution.SetProvider(() => throw new InvalidOperationException("no key"));
            LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.GitCli, 2, 3, 4);
        }
        Assert.Equal(0, walks);
        Assert.Empty(attribution.SiteSnapshot());
        Assert.Empty(attribution.Snapshot());
        var snapshot = counters.Snapshot();
        var original = LaunchLockTelemetry.FormatSummary(snapshot, 42) + Environment.NewLine;
        var actual = string.Join(Environment.NewLine, LaunchLockTelemetry.FormatTeardownLines(snapshot, attribution, 42))
            + Environment.NewLine;
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(original), System.Text.Encoding.UTF8.GetBytes(actual));
        using var diagnostics = new StringWriter();
        await new AssemblyTempRootCleanupFixture(() => null, diagnostics, attribution: attribution).DisposeAsync();
        Assert.StartsWith("LAUNCH_LOCK_SUMMARY ", Assert.Single(
            diagnostics.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public async Task GitContext_NestedAndConcurrent_RestoresWithoutLeaking()
    {
        using (LaunchLockSiteCapture.BeginGit(["status"]))
        {
            Assert.Equal("status", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
            using (LaunchLockSiteCapture.BeginGit(["log"]))
                Assert.Equal("log", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
            Assert.Equal("status", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
            Assert.Equal("-", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.Start).Git);
        }
        Assert.Equal("-", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
        using var ready = new Barrier(2);
        var workers = new[] { "status", "log" }.Select(command => Task.Factory.StartNew(() =>
        {
            using (LaunchLockSiteCapture.BeginGit([command]))
            {
                Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(30)), "Both git contexts did not become ready.");
                Assert.Equal(command, LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal("-", LaunchLockSiteCapture.Capture(LaunchLockEntryPoint.GitCli).Git);
    }

    [Fact]
    public void RecordInto_CaptureFailure_PreservesLaunchAndClassCounts()
    {
        var attribution = new LaunchLockAttribution(_ => throw new InvalidOperationException("capture"));
        attribution.SetProvider(() => "A");
        var counters = new LaunchLockCounters();
        LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.Start, 0, 0, 1);
        Assert.Equal(1, counters.Snapshot().Launches);
        Assert.Equal(1, Assert.Single(attribution.Snapshot()).Launches);
        Assert.Equal(new LaunchLockSite("Unknown.Unknown", "-"), Assert.Single(attribution.SiteSnapshot()).Site);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static GitCli.GitResult RunGit(string root) =>
        GitCli.Run(root, "-c", "core.quotepath=false", "-c", "a.b=c", "version");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RecordAsync(LaunchLockAttribution attribution)
    {
        LaunchLockTelemetry.RecordInto(new(), attribution, LaunchLockEntryPoint.Start, 0, 0);
        await Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IEnumerable<int> RecordIterator(LaunchLockAttribution attribution)
    {
        LaunchLockTelemetry.RecordInto(new(), attribution, LaunchLockEntryPoint.Start, 0, 0);
        yield return 1;
    }
}
