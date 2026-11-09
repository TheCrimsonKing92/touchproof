using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private attribution and synthetic snapshots only.
public sealed class LaunchLockSiteFormatterTests
{
    [Fact]
    public void Teardown_TwelveRows_EmitsTenHeaviestSiteSubcommandPairs()
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        for (var i = 11; i >= 0; i--)
        for (var n = 0; n < 12 - i; n++)
            attribution.Record(LaunchLockEntryPoint.GitCli, 0,
                new(i < 2 ? "Shared.Call" : $"Site{i}.Call", $"cmd{i}"));
        Assert.Equal(12, attribution.SiteSnapshot().Count);
        var lines = LaunchLockTelemetry.FormatTeardownLines(new LaunchLockCounters().Snapshot(), attribution, 42).ToArray();
        Assert.Equal(12, lines.Length);
        Assert.StartsWith("LAUNCH_LOCK_SUMMARY ", lines[0]);
        Assert.StartsWith("LAUNCH_LOCK_BY_CLASS ", lines[1]);
        Assert.Equal(Enumerable.Range(0, 10).Select(i =>
            $"LAUNCH_LOCK_BY_SITE pid=42 class=A site={(i < 2 ? "Shared.Call" : $"Site{i}.Call")} git=cmd{i} launches={12 - i}"),
            lines.Skip(2));
    }

    [Fact]
    public void Teardown_FiftyOneClasses_UsesExistingClassSelectionAndOrder()
    {
        var classes = Enumerable.Range(0, 51).Select(i =>
            new LaunchLockClassSnapshot($"Class{i:D2}", i + 1, 0, new long[5])).ToArray();
        var sites = classes.Select(row => new LaunchLockSiteSnapshot(row.Key, new("Helper.Call", "status"), row.Launches)).ToArray();
        var lines = LaunchLockSiteFormatter.FormatBySite(classes, sites, 7).ToArray();
        Assert.Equal(Enumerable.Range(0, 50).Select(i =>
            $"LAUNCH_LOCK_BY_SITE pid=7 class=Class{50 - i:D2} site=Helper.Call git=status launches={51 - i}"), lines);
        Assert.Equal(LaunchLockTelemetry.FormatByClass(classes, 7).Select(ClassToken), lines.Select(ClassToken));
    }

    [Fact]
    public void FormatBySite_TiesAndWhitespace_UsesOrdinalOrderAndSafeTokens()
    {
        LaunchLockClassSnapshot[] classes = [new("", 4, 0, new long[5]), new("Z\r\n Class", 3, 0, new long[5])];
        LaunchLockSiteSnapshot[] sites =
        [
            new("", new("B.Call", "a"), 1), new("", new("A.Call", "z"), 1),
            new("", new("A.Call", "a"), 1), new("", new("A.Call", "-"), 1),
            new("Z\r\n Class", new("Space .Call", "sub command"), 3)
        ];
        Assert.Equal(new[]
        {
            "LAUNCH_LOCK_BY_SITE pid=1 class=(none) site=A.Call git=- launches=1",
            "LAUNCH_LOCK_BY_SITE pid=1 class=(none) site=A.Call git=a launches=1",
            "LAUNCH_LOCK_BY_SITE pid=1 class=(none) site=A.Call git=z launches=1",
            "LAUNCH_LOCK_BY_SITE pid=1 class=(none) site=B.Call git=a launches=1",
            "LAUNCH_LOCK_BY_SITE pid=1 class=Z___Class site=Space_.Call git=sub_command launches=3"
        }, LaunchLockSiteFormatter.FormatBySite(classes, sites, 1));
    }

    private static string ClassToken(string line) => line.Split(' ')[2];
}
