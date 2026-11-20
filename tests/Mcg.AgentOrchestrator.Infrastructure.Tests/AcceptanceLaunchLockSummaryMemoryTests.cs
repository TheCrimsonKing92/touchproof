using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uniquely owned directory and hand-written lane receipts.
public sealed class AcceptanceLaunchLockSummaryMemoryTests
{
    [Fact]
    public void Preserve_KeepsOnlyMemoryRowsWithMatchingPid()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            const string summary = "LAUNCH_LOCK_SUMMARY pid=42 launches=1";
            const string first = "LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=A heap_start_bytes=100 heap_end_bytes=300 private_start_bytes=1000 private_end_bytes=3000";
            const string second = "LAUNCH_LOCK_MEMORY_BY_CLASS pid=42 class=B heap_start_bytes=200 heap_end_bytes=400 private_start_bytes=2000 private_end_bytes=4000";
            var stderr = string.Join('\n', "LAUNCH_LOCK_SUMMARY pid=7 launches=1",
                first.Replace("pid=42", "pid=7"), summary, first, second,
                first.Replace("pid=42", "pid=420"), first.Replace("pid=42", "pid=+42"),
                first.Replace(" pid=42", ""), first.Replace("pid=42", "pid=unknown"));

            AcceptanceLaunchLockSummary.Preserve("unused", stderr, Path.Combine(root, "lane.trx"), Path.Combine(root, "attempt"));

            Assert.Equal(new[] { summary, first, second }, File.ReadAllLines(Path.Combine(root, "lane.launch-lock-summary.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
