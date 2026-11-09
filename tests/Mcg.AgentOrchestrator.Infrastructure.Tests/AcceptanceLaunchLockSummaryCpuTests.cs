using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uniquely owned directories and hand-written lane receipts.
public sealed class AcceptanceLaunchLockSummaryCpuTests
{
    [Fact]
    public void Preserve_KeepsLaneCpuRowsAndDropsNestedHostRows()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            const string summary = "LAUNCH_LOCK_SUMMARY pid=42 launches=1";
            const string cpu = "LAUNCH_LOCK_CPU_BY_CLASS pid=42 class=A child_cpu_ms=1.250 inproc_cpu_ms_apportioned=2.500";
            const string hostOnly = "LAUNCH_LOCK_CPU_BY_CLASS pid=42 class=B child_cpu_ms=0.000 inproc_cpu_ms_apportioned=3.000";
            const string launches = "LAUNCH_LOCK_BY_CLASS pid=42 class=A launches=1";
            var output = "LAUNCH_LOCK_SUMMARY pid=7 launches=1\n" + cpu.Replace("pid=42", "pid=7") +
                $"\n{summary}\n{launches}\n{cpu}\n{hostOnly}\n" + cpu.Replace("pid=42", "pid=420");
            AcceptanceLaunchLockSummary.Preserve("unused", output, Path.Combine(root, "lane.trx"), Path.Combine(root, "attempt"));
            Assert.Equal(string.Join(Environment.NewLine, summary, launches, cpu, hostOnly, ""),
                File.ReadAllText(Path.Combine(root, "lane.launch-lock-summary.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
