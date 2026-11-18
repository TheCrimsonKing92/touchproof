using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: contracts read immutable snapshots and own their declaration collections.
public sealed class ExperimentReplayRecordingContractTests
{
    [Fact]
    public void GetMissingInputs_CompleteAccounting_ReturnsEmpty()
    {
        var contract = ExperimentReplayRecordingContract.WorkerDispatchCpu;

        Assert.Equal("worker-dispatch-cpu", contract.CapabilityId);
        Assert.Empty(contract.GetMissingInputs(Snapshot(new(10, 100, 0, AccountingSource: "job-object"))));
    }

    [Fact]
    public void GetMissingInputs_NoAccounting_ReturnsAllNamesInDeclarationOrder()
    {
        Assert.Equal(new[] { "cpu-milliseconds", "peak-memory-bytes", "accounting-source" },
            ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(Snapshot(null)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetMissingInputs_BlankSource_ReturnsOnlyAccountingSource(string? source)
    {
        Assert.Equal(new[] { "accounting-source" },
            ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(
                Snapshot(new(10, 100, 0, AccountingSource: source!))));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void GetMissingInputs_CpuBoundary_EvaluatesIndependently(long cpu, bool missing)
    {
        var result = ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(
            Snapshot(new(cpu, 100, 0)));

        Assert.Equal(missing ? new[] { "cpu-milliseconds" } : Array.Empty<string>(), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void GetMissingInputs_NonPositiveMemory_ReturnsOnlyPeakMemory(long memory)
    {
        Assert.Equal(new[] { "peak-memory-bytes" },
            ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(Snapshot(new(10, memory, 0))));
    }

    [Fact]
    public void GetMissingInputs_NonAdjacentMissingInputs_PreservesDeclarationOrder()
    {
        Assert.Equal(new[] { "cpu-milliseconds", "accounting-source" },
            ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(
                Snapshot(new(-1, 100, 0, AccountingSource: ""))));
    }

    [Fact]
    public void GetMissingInputs_NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ExperimentReplayRecordingContract.WorkerDispatchCpu.GetMissingInputs(null!));
    }

    [Fact]
    public void GetMissingInputs_CustomDeclarations_EvaluatesEachOnceInOrder()
    {
        var visited = new List<string>();
        var contract = new ExperimentReplayRecordingContract("custom",
        [
            new("z", _ => { visited.Add("z"); return false; }),
            new("a", _ => { visited.Add("a"); return true; }),
            new("m", _ => { visited.Add("m"); return false; })
        ]);

        Assert.Equal(new[] { "z", "m" }, contract.GetMissingInputs(Snapshot(null)));
        Assert.Equal(new[] { "z", "a", "m" }, visited);
    }

    [Fact]
    public void GetMissingInputs_PredicateThrows_PropagatesEvaluationFailure()
    {
        var failure = new InvalidOperationException("evaluation failed");
        var contract = new ExperimentReplayRecordingContract("custom", [new("input", _ => throw failure)]);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => contract.GetMissingInputs(Snapshot(null))));
    }

    [Fact]
    public void Constructor_CopiesDeclarations_AndCollectionsRejectMutation()
    {
        var declarations = new[] { new ExperimentReplayRecordingContract.Input("original", _ => false) };
        var contract = new ExperimentReplayRecordingContract("custom", declarations);
        declarations[0] = new("replacement", _ => true);

        Assert.Equal("original", Assert.Single(contract.Inputs).Name);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ExperimentReplayRecordingContract.Input>)contract.Inputs).Clear());
        var missing = contract.GetMissingInputs(Snapshot(null));
        Assert.Equal(new[] { "original" }, missing);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)missing).Clear());
        Assert.Equal(new[] { "original" }, contract.GetMissingInputs(Snapshot(null)));
    }

    [Fact]
    public void Constructor_InvalidDeclarations_RejectsMalformedContracts()
    {
        Assert.Throws<ArgumentNullException>(() => new ExperimentReplayRecordingContract(null!, []));
        Assert.Throws<ArgumentException>(() => new ExperimentReplayRecordingContract(" ", []));
        Assert.Throws<ArgumentNullException>(() => new ExperimentReplayRecordingContract("custom", null!));
        Assert.Throws<ArgumentException>(() => new ExperimentReplayRecordingContract("custom", [null!]));
        Assert.Throws<ArgumentException>(() => new ExperimentReplayRecordingContract("custom", [new(" ", _ => true)]));
        Assert.Throws<ArgumentException>(() => new ExperimentReplayRecordingContract("custom", [new("input", null!)]));
        Assert.Throws<ArgumentException>(() => new ExperimentReplayRecordingContract("custom",
            [new("input", _ => true), new("input", _ => false)]));
    }

    private static TaskProcessSnapshot Snapshot(TaskProcessResourceAccountingSnapshot? accounting) => new(
        1, "worker", "work", "stdout", "stderr", "exit", DateTimeOffset.UnixEpoch, null, null,
        ResourceAccounting: accounting);
}
