using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsRunningWorkerOutputReads(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(1_048_576, 1_048_576)]
    [InlineData(10_485_760, 10_485_760)]
    [InlineData(1_048_576, 10_485_760)]
    public void RunningWorkerSecondTickReadsOnlyAppendedBytes(int initialBytes, int nextBytes)
    {
        using var fixture = new HeldGoalWorkerFixture(initialBytes);
        var loop = fixture.Loop();
        var first = ConductorBatchLoopTestsStepLedger.OneTick(loop, fixture.Kernel, fixture.Driver);
        Assert.Equal(1, first.Held);
        Assert.Equal(initialBytes, fixture.BytesRead); // Proves the production refresh selected the injected reader.
        fixture.GrowTo(nextBytes);
        var before = fixture.BytesRead;
        var second = ConductorBatchLoopTestsStepLedger.OneTick(loop, fixture.Kernel, fixture.Driver);
        Assert.Equal(1, second.Held);
        Assert.Equal(nextBytes - initialBytes, fixture.BytesRead - before);
        Assert.True(fixture.Task.LastProcess!.IsRunning);
        output.WriteLine(ConductorBatchLoopTestsStepLedger.Phase(first, "per-goal-walk"));
        output.WriteLine(ConductorBatchLoopTestsStepLedger.Phase(second, "per-goal-walk"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerExitBetweenTicksIsReconciled(bool priorHold)
    {
        using var fixture = new HeldGoalWorkerFixture(1_048_576);
        var loop = fixture.Loop();
        if (priorHold) Assert.Equal(1, ConductorBatchLoopTestsStepLedger.OneTick(loop, fixture.Kernel, fixture.Driver).Held);
        File.WriteAllText(fixture.Process.ExitCodePath, "7");
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, fixture.Kernel, fixture.Driver);
        Assert.Equal(7, fixture.Task.LastProcess!.ExitCode);
        Assert.False(fixture.Task.LastProcess.IsRunning);
        // This output has no Reviewer terminal contract, so completion requests a same-role repair.
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Null(fixture.Task.LastVerification);
        var verification = Assert.Single(fixture.Task.VerificationHistory);
        Assert.Equal(7, verification.ExitCode);
        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete, DispatchFailureClassifier.Classify(fixture.Task, verification).Kind);
        Assert.Equal(RetryCause.ProviderInterruption, fixture.Task.PendingRetryCause);
        Assert.Single(fixture.Goal.Timeline.Where(entry => entry.TaskId == fixture.Task.Id && entry.Kind == ProgressKind.TaskRetried));
        Assert.Equal(1, tick.Advanced);
        Assert.Equal(0, tick.Escalated);
    }

    [Fact]
    public void IncrementalSnapshotsMatchFreshReadsAcrossTextBoundariesAndRetentionCap()
    {
        using var fixture = new HeldGoalWorkerFixture(0);
        var reader = new ProcessLogReader();
        var path = fixture.Process.StandardOutputPath;
        var pieces = new[]
        {
            Encoding.UTF8.GetPreamble(), Encoding.UTF8.GetBytes("prefix\r"), Encoding.UTF8.GetBytes("\nWORKER_RES"),
            Encoding.UTF8.GetBytes("ULT:\nfiles: café "), new byte[] { 0xf0, 0x9f }, new byte[] { 0x98, 0x80 },
            Encoding.UTF8.GetBytes("\r\n" + string.Concat(Enumerable.Repeat("tests: deferred - SomeTests\n", 30_000))),
            Encoding.UTF8.GetBytes("END_WORKER_RESULT\r\nTokens us"), Encoding.UTF8.GetBytes("ed: 1\n")
        };
        foreach (var bytes in pieces)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) stream.Write(bytes);
            var incremental = reader.ReadBestEffort(fixture.Process, path);
            Assert.Equal(new ProcessLogReader().ReadBestEffort(fixture.Process, path), incremental);
        }
        var capped = reader.ReadBestEffort(fixture.Process, path);
        Assert.Contains("files: café 😀", capped.DecisionText);
        Assert.True(capped.DecisionTruncatedChars > 0);
        File.WriteAllText(path, "tokens us");
        Assert.False(reader.ReadBestEffort(fixture.Process, path).FinalOutputSeen);
        File.AppendAllText(path, "ed: 1");
        Assert.True(reader.ReadBestEffort(fixture.Process, path).FinalOutputSeen);
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("replace")]
    [InlineData("rewrite")]
    [InlineData("new-attempt")]
    [InlineData("new-start")]
    [InlineData("evict")]
    [InlineData("missing")]
    public void OutputIdentityChangesResetTheCursor(string change)
    {
        using var fixture = new HeldGoalWorkerFixture(1_048_576);
        var path = fixture.Process.StandardOutputPath;
        long read = 0;
        var reader = new ProcessLogReader(p => new CountingLogReadStream(File.OpenRead(p), bytes => read += bytes));
        reader.ReadBestEffort(fixture.Process, path);
        var process = fixture.Process;
        switch (change)
        {
            case "truncate": File.WriteAllText(path, "HUMAN_INPUT: truncated"); break;
            case "replace":
                File.WriteAllText(path + ".replacement", new string('a', 1_048_576));
                File.Move(path + ".replacement", path, overwrite: true); break;
            case "rewrite":
                File.WriteAllText(path, new string('b', 1_048_576));
                File.SetLastWriteTimeUtc(path, fixture.UtcNow.UtcDateTime); break;
            case "new-attempt": process = process with { ExitCodePath = path + ".new-exit" }; break;
            case "new-start": process = process with { StartedAt = process.StartedAt.AddMinutes(1) }; break;
            case "evict": reader.Evict(process); break;
            case "missing":
                File.Delete(path);
                Assert.Equal(string.Empty, reader.ReadBestEffort(process, path).DecisionText);
                File.WriteAllText(path, "HUMAN_INPUT: recreated"); break;
        }
        read = 0;
        var actual = reader.ReadBestEffort(process, path);
        Assert.Equal(new FileInfo(path).Length, read);
        Assert.Equal(new ProcessLogReader().ReadBestEffort(process, path), actual);
    }

    [Fact]
    public void TransientReadFailureDoesNotDiscardTheLastSuccessfulCursor()
    {
        using var fixture = new HeldGoalWorkerFixture(1_048_576);
        var failRead = false;
        long read = 0;
        var reader = new ProcessLogReader(path =>
        {
            if (failRead) throw new IOException("fixture locked log");
            return new CountingLogReadStream(File.OpenRead(path), bytes => read += bytes);
        });
        var first = reader.ReadBestEffort(fixture.Process, fixture.Process.StandardOutputPath);
        Assert.Same(first, reader.ReadBestEffort(fixture.Process, fixture.Process.StandardOutputPath));
        fixture.GrowTo(1_048_656);
        failRead = true;
        Assert.False(reader.ReadBestEffort(fixture.Process, fixture.Process.StandardOutputPath).ReadSucceeded);
        failRead = false;
        read = 0;
        var resumed = reader.ReadBestEffort(fixture.Process, fixture.Process.StandardOutputPath);
        Assert.True(resumed.ReadSucceeded);
        Assert.Equal(80, read);
        Assert.Equal(new ProcessLogReader().ReadBestEffort(fixture.Process, fixture.Process.StandardOutputPath), resumed);
    }
}
