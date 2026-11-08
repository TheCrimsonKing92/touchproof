using System.Text.RegularExpressions;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class WorkerBuildScopeToolCacheTests
{
    private readonly Xunit.ITestOutputHelper _output;
    public WorkerBuildScopeToolCacheTests(Xunit.ITestOutputHelper output) => _output = output;

    [Xunit.Fact]
    public void RepeatedHelper_CorruptEntryAndConcurrentMisses_ReuseValidOutput()
    {
        using var fixture = new WorkerBuildScopeToolCacheFixture();
        var work = fixture.Work("work");
        var log = Path.Combine(fixture.Parent, "vectors.txt");
        var first = WorkerBuildScopeToolCacheFixture.Run(fixture.Helper(work, "real", log));
        Success(first);
        var miss = CacheLine(first.Stdout);
        Assert.Matches(@"^scope-tool: cache=miss digest=[a-f0-9]{12} build_ms=\d+$", miss);
        Assert.Single(BuildVectors(log)); // positive control: this logger sees real bootstraps
        var entry = Assert.Single(Directory.GetDirectories(fixture.Cache));
        WorkerBuildScopeToolCacheFixture.ValidEntry(entry);
        Assert.Contains("digest=" + Path.GetFileName(entry)[..12], miss);

        File.Delete(log);
        var second = WorkerBuildScopeToolCacheFixture.Run(fixture.Helper(work, "real", log));
        Success(second);
        Assert.Equal("scope-tool: cache=hit digest=" + Path.GetFileName(entry)[..12], CacheLine(second.Stdout));
        Assert.Empty(BuildVectors(log));
        Assert.Equal(ScopeLine(first.Stdout), ScopeLine(second.Stdout));
        Assert.True(first.Stdout.IndexOf(miss, StringComparison.Ordinal) < first.Stdout.IndexOf(ScopeLine(first.Stdout), StringComparison.Ordinal));

        var seed = Path.Combine(fixture.Parent, "seed");
        WorkerBuildScopeToolCacheFixture.CopyDirectory(entry, seed);
        var dll = Path.Combine(entry, "WorkerBuildScopeTool.dll");
        var bytes = File.ReadAllBytes(dll);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(dll, bytes);
        File.Delete(log);
        var repaired = WorkerBuildScopeToolCacheFixture.Run(fixture.Helper(work, "real", log));
        Success(repaired);
        Assert.Equal(Digest(miss), Digest(CacheLine(repaired.Stdout)));
        Assert.Contains("cache=miss", CacheLine(repaired.Stdout));
        Assert.Single(BuildVectors(log));
        WorkerBuildScopeToolCacheFixture.ValidEntry(entry);
        Assert.Single(Directory.GetDirectories(fixture.Cache));
        File.Delete(log);
        var afterRepair = WorkerBuildScopeToolCacheFixture.Run(fixture.Helper(work, "real", log));
        Success(afterRepair);
        Assert.Equal(CacheLine(second.Stdout), CacheLine(afterRepair.Stdout));
        Assert.Empty(BuildVectors(log));

        // A seed copy avoids two redundant real compilations. Both helper processes
        // must reach the bootstrap vector before either is allowed to publish.
        Directory.Delete(entry, recursive: true);
        var name = "Local\\scope-cache-" + Guid.NewGuid().ToString("N");
        using var readyOne = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-one");
        using var readyTwo = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-two");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-release");
        var startOne = fixture.Helper(fixture.Work("one"), "copy", Path.Combine(fixture.Parent, "one.txt"));
        var startTwo = fixture.Helper(fixture.Work("two"), "copy", Path.Combine(fixture.Parent, "two.txt"));
        foreach (var start in new[] { startOne, startTwo })
        {
            start.Environment["CACHE_SEED"] = seed;
            start.Environment["CACHE_RELEASE"] = name + "-release";
        }
        startOne.Environment["CACHE_READY"] = name + "-one";
        startTwo.Environment["CACHE_READY"] = name + "-two";
        using var captureOne = TestChildProcessCapture.Start(startOne, WorkerBuildScopeToolCacheFixture.Start);
        using var captureTwo = TestChildProcessCapture.Start(startTwo, WorkerBuildScopeToolCacheFixture.Start);
        try
        {
            Assert.True(readyOne.WaitOne(TimeSpan.FromMinutes(5)), "First helper never reached the bootstrap barrier.");
            Assert.True(readyTwo.WaitOne(TimeSpan.FromMinutes(5)), "Second helper never reached the bootstrap barrier.");
        }
        finally { release.Set(); }
        var concurrentOne = captureOne.Complete(TimeSpan.FromMinutes(10));
        var concurrentTwo = captureTwo.Complete(TimeSpan.FromMinutes(10));
        foreach (var result in new[] { concurrentOne, concurrentTwo })
        {
            Success(result);
            Assert.Contains("cache=miss", CacheLine(result.Stdout));
            Assert.Equal(Digest(miss), Digest(CacheLine(result.Stdout)));
        }
        Assert.Single(BuildVectors(Path.Combine(fixture.Parent, "one.txt")));
        Assert.Single(BuildVectors(Path.Combine(fixture.Parent, "two.txt")));
        WorkerBuildScopeToolCacheFixture.ValidEntry(Assert.Single(Directory.GetDirectories(fixture.Cache)));
    }

    private void Success(TestChildProcessCapture.Result result)
    {
        _output.WriteLine(result.Stdout + result.Stderr);
        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        Assert.Contains("PASS build: 0 errors", result.Stdout);
    }

    private static string CacheLine(string text) => Assert.Single(text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("scope-tool:", StringComparison.Ordinal)));
    private static string ScopeLine(string text) => Assert.Single(text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("build-scope:", StringComparison.Ordinal)));
    private static string Digest(string line) => Regex.Match(line, @"digest=([a-f0-9]{12})").Groups[1].Value;
    private static string[] BuildVectors(string log) => File.Exists(log) ? File.ReadAllLines(log).Where(line => line.Contains("WorkerBuildScopeTool.csproj", StringComparison.Ordinal)).ToArray() : [];
}
