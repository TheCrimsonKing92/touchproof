using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: injected host performs no process, Git or test-host operations.
public sealed class ExperimentReplayExecutionTests
{
    [Fact]
    public void Schedule_FixedSeed_HasWarmupsAndThreeMeasuredPairsPerCase()
    {
        var schedule = ExperimentReplaySchedule.Create(Registration());
        Assert.Equal(schedule, ExperimentReplaySchedule.Create(Registration()));
        Assert.Equal(232, schedule.Count);
        Assert.Equal(20, schedule.Count(s => s.Stage == "AA" && !s.Warmup));
        foreach (var item in Registration().Cases)
        {
            var slots = schedule.Where(s => s.CaseId == item.Id && s.Stage == "AB").ToArray();
            Assert.Equal(4, slots.Count(s => s.Warmup));
            Assert.Equal(6, slots.Count(s => !s.Warmup));
        }
        Assert.All(schedule.Chunk(2), pair => Assert.Equal(new[] { "A", "B" }, pair.Select(s => s.Arm).Order()));
    }

    [Fact]
    public async Task Runner_ValidHost_RecordsEverySlotAndCleansAllCases()
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        store.Register(Registration());
        var host = new Host();
        var run = await ExperimentReplayRunner.RunAsync(store, Registration(), fixture.Root, host, default);
        Assert.Equal("complete", run.State);
        Assert.Equal(232, store.Executions(run.Id).Count);
        Assert.Equal(20, host.Prepared);
        Assert.Equal(host.Prepared, host.Cleaned);
        Assert.Equal(1, host.MaximumConcurrent);
    }

    [Fact]
    public async Task Runner_InvalidAccounting_PreservesReceiptAndStopsWithoutRetry()
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        store.Register(Registration());
        var host = new Host { Invalid = true };
        var run = await ExperimentReplayRunner.RunAsync(store, Registration(), fixture.Root, host, default);
        Assert.Equal("inconclusive", run.State);
        Assert.Null(Assert.Single(store.Executions(run.Id)).Resources);
        Assert.Equal(1, host.Cleaned);
    }

    [Fact]
    public async Task Runner_CancelledProcedure_PreservesPartialResourcesAndCleans()
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        store.Register(Registration());
        var host = new Host { Cancel = true };
        var run = await ExperimentReplayRunner.RunAsync(store, Registration(), fixture.Root, host, default);
        Assert.Equal("incomplete", run.State);
        var receipt = Assert.Single(store.Executions(run.Id));
        Assert.Equal("cancelled", receipt.State);
        Assert.Equal(100, receipt.Resources!.CpuMilliseconds);
        Assert.Equal(1, host.Cleaned);
        Assert.Throws<InvalidOperationException>(() => store.Record(receipt with { Id = "late" }));
    }

    private sealed class Host : ExperimentReplayRunner.IHost
    {
        internal int Prepared, Cleaned, MaximumConcurrent;
        internal bool Invalid, Cancel;
        private int _active;
        public Task<ExperimentReplayRunner.Prepared> PrepareAsync(ExperimentReplayCase item, string artifacts, CancellationToken token)
        { Prepared++; return Task.FromResult(new ExperimentReplayRunner.Prepared(item.Id, artifacts)); }
        public Task<ExperimentReplayExecution> ExecuteAsync(ExperimentReplayRegistration registration, ExperimentReplayRun run,
            ExperimentReplaySlot slot, ExperimentReplayRunner.Prepared prepared, string artifacts, CancellationToken token)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, ++_active);
            var execution = Execution(slot) with { RunId = run.Id, Resources = Invalid ? null : Execution(slot).Resources,
                State = Cancel ? "cancelled" : "passed" };
            _active--;
            return Task.FromResult(execution);
        }
        public void Cleanup(ExperimentReplayRunner.Prepared prepared) => Cleaned++;
    }
}
