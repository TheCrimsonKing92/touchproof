using System.ComponentModel;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private counters/scopes; the launcher test reads only this class's live key.
public sealed class LaunchLockImageAttributionTests
{
    [Fact]
    public void Teardown_TwoClasses_EmitsExactImagesBesideClassLines()
    {
        var counters = new LaunchLockCounters();
        var attribution = new LaunchLockAttribution();
        var key = "ClassA";
        attribution.SetProvider(() => key);
        Record(@"C:\tools\GIT.EXE", 3);
        Record("icacls", 2);
        Record("powershell.exe", 1);
        Record("robocopy.exe", 1);
        key = "ClassB";
        Record("/usr/bin/git", 1);
        Record("icacls.exe", 1);
        Record("powershell", 2);
        // No scope remains: this launch must go to other.
        LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.Start, 0, 0);

        var lines = LaunchLockTelemetry.FormatTeardownLines(counters.Snapshot(), attribution, 4242).ToArray();
        Assert.Equal(new[]
        {
            "LAUNCH_LOCK_BY_IMAGE pid=4242 class=ClassA git=3 powershell=1 pwsh=0 icacls=2 cmd=0 dotnet=0 other=1",
            "LAUNCH_LOCK_BY_IMAGE pid=4242 class=ClassB git=1 powershell=2 pwsh=0 icacls=1 cmd=0 dotnet=0 other=1"
        }, lines.Where(line => line.StartsWith("LAUNCH_LOCK_BY_IMAGE ", StringComparison.Ordinal)));
        foreach (var row in attribution.Snapshot())
        {
            var classIndex = Array.FindIndex(lines, line =>
                line.StartsWith($"LAUNCH_LOCK_BY_CLASS pid=4242 class={row.Key} ", StringComparison.Ordinal));
            Assert.True(classIndex >= 0);
            Assert.StartsWith($"LAUNCH_LOCK_BY_IMAGE pid=4242 class={row.Key} ", lines[classIndex + 1]);
            Assert.Equal(row.Launches, attribution.ImageSnapshot()[row.Key].Sum());
        }
        Assert.Equal(12, counters.Snapshot().Launches);

        void Record(string image, int count)
        {
            using var scope = LaunchLockImageCounters.Begin(image);
            for (var i = 0; i < count; i++)
                LaunchLockTelemetry.RecordInto(counters, attribution, LaunchLockEntryPoint.GitCli, 0, 0);
        }
    }

    [Theory]
    [InlineData(" \"C:\\tools\\GIT.EXE\" ", 0)]
    [InlineData("/usr/bin/PowerShell.EXE", 1)]
    [InlineData("/usr/bin/pwsh", 2)]
    [InlineData("icacls.exe", 3)]
    [InlineData("CMD.Exe", 4)]
    [InlineData("dotnet", 5)]
    [InlineData("git.wrapper.exe", 6)]
    [InlineData("", 6)]
    [InlineData(null, 6)]
    public void Record_FileName_CountsOnlyTheExpectedBucket(string? fileName, int bucket)
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        using (LaunchLockImageCounters.Begin(fileName)) attribution.Record(LaunchLockEntryPoint.Start, 0);
        var expected = new long[7];
        expected[bucket] = 1;
        Assert.Equal(expected, attribution.ImageSnapshot()["A"]);
    }

    [Fact]
    public void Record_NestedScopesAndRejectedKeys_RestoresImageWithoutExtraCounts()
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        using (LaunchLockImageCounters.Begin("git"))
        {
            attribution.Record(LaunchLockEntryPoint.Start, 0);
            using (LaunchLockImageCounters.Begin("pwsh")) attribution.Record(LaunchLockEntryPoint.Start, 0);
            attribution.Record(LaunchLockEntryPoint.Start, 0);
        }
        attribution.Record(LaunchLockEntryPoint.Start, 0);
        attribution.SetProvider(() => null);
        attribution.Record(LaunchLockEntryPoint.Start, 0);
        attribution.SetProvider(() => throw new InvalidOperationException());
        attribution.Record(LaunchLockEntryPoint.Start, 0);
        attribution.SetProvider(null);
        attribution.Record(LaunchLockEntryPoint.Start, 0);
        Assert.Equal(new long[] { 2, 0, 1, 0, 0, 0, 1 }, attribution.ImageSnapshot()["A"]);
        Assert.Equal(4, Assert.Single(attribution.Snapshot()).Launches);
    }

    [Fact]
    public void Record_ConcurrentScopes_KeepsAllImageAndClassCounts()
    {
        var attribution = new LaunchLockAttribution();
        attribution.SetProvider(() => "A");
        Parallel.For(0, 1000, i =>
        {
            using var scope = LaunchLockImageCounters.Begin(i % 2 == 0 ? "git" : "icacls");
            attribution.Record(LaunchLockEntryPoint.Start, 0);
        });
        Assert.Equal(new long[] { 500, 0, 0, 500, 0, 0, 0 }, attribution.ImageSnapshot()["A"]);
        Assert.Equal(1000, Assert.Single(attribution.Snapshot()).Launches);
    }

    [Fact]
    public void Start_MissingExecutable_RecordsItsImageAndPreservesFailure()
    {
        if (!OperatingSystem.IsWindows()) return;
        var attribution = LaunchLockTelemetry.Attribution;
        Assert.True(attribution.IsActive);
        var key = TestContext.Current.TestClass?.TestClassName;
        Assert.Equal(nameof(LaunchLockImageAttributionTests), key);
        var beforeImages = attribution.ImageSnapshot().GetValueOrDefault(key!) ?? new long[7];
        var beforeLaunches = attribution.Snapshot().SingleOrDefault(row => row.Key == key)?.Launches ?? 0;
        var fileName = Path.Combine(Path.GetTempPath(), "missing-image-" + Guid.NewGuid().ToString("N"), "icacls.exe");
        Assert.False(File.Exists(fileName));
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        Assert.Throws<Win32Exception>(() => HandleListProcessLauncher.Start(info));
        Assert.Equal(fileName, info.FileName);
        var expected = (long[])beforeImages.Clone();
        expected[3]++;
        Assert.Equal(expected, attribution.ImageSnapshot()[key!]);
        Assert.Equal(beforeLaunches + 1, attribution.Snapshot().Single(row => row.Key == key).Launches);
    }
}
