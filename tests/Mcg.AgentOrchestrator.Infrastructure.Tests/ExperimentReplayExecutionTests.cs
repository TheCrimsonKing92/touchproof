using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using Xunit;
using static ExperimentReplayTestData;

// Parallel-safe: injected execution host and private repositories/checkout identities; no test host.
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

    [Theory]
    [InlineData("cancelled", "incomplete", 1, "inconclusive; operator review required")]
    [InlineData("timed-out", "incomplete", 1, "inconclusive; operator review required")]
    [InlineData("passed", "complete", 2, "descriptive-complete; no winner or deployment claim")]
    public async Task Runner_ReferenceOutcome_RetainsReceiptAndControlsContinuation(
        string referenceState, string runState, int referenceCount, string decision)
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        var registration = Registration();
        registration = registration with
        {
            Cases = registration.Cases.Select((item, index) => index < 2 ? item with
            {
                Gates = [new("gate-" + index, Now, "test-failed", ["ExampleTests.Failure"], [])]
            } : item).ToArray()
        };
        store.Register(registration);
        var host = new Host { ReferenceState = referenceState };
        var run = await ExperimentReplayRunner.RunAsync(store, registration, fixture.Root, host, default);

        Assert.Equal(runState, run.State);
        Assert.Equal(run, store.Run(run.Id));
        var executions = store.Executions(run.Id);
        var references = executions.Where(e => e.Slot.Stage == "reference").ToArray();
        Assert.Equal(referenceCount, host.ReferenceCalls);
        Assert.Equal(referenceCount, references.Length);
        Assert.Equal(ExperimentReplaySchedule.Create(registration).Count + referenceCount, executions.Count);
        var receipt = references[0];
        Assert.Equal(registration.Cases[0].Id, receipt.Slot.CaseId);
        Assert.Equal(referenceState, receipt.State);
        Assert.Equal(receipt.Reason, run.Reason);
        Assert.Equal(100, receipt.Resources!.CpuMilliseconds);
        Assert.Equal(1024, receipt.Resources.PeakMemoryBytes);
        Assert.Equal(50, receipt.Resources.WallMilliseconds);
        Assert.Equal(1, receipt.Resources.LaunchesByExecutable["dotnet"]);
        Assert.Equal("job-object/live", receipt.Resources.AccountingSource);
        Assert.Contains(new ExperimentReplaySource("reference-receipt", "hash"), receipt.Sources);
        Assert.Equal(20, host.Prepared);
        Assert.Equal(host.Prepared, host.Cleaned);
        var report = ExperimentReplayReportWriter.Create(registration, store.Run(run.Id), executions);
        Assert.Equal("passed", report.Calibration.State);
        Assert.Equal(20, report.ValidCases);
        Assert.Equal(decision, report.Decision);
    }

    [Theory]
    [InlineData("toolchain", "cancelled")]
    [InlineData("toolchain", "timed-out")]
    [InlineData("prerequisites", "cancelled")]
    [InlineData("prerequisites", "timed-out")]
    public async Task Runner_InterruptedPreparation_RetainsStateResourcesAndSource(string phase, string state)
    {
        using var fixture = new ExperimentReplayTestData();
        var store = fixture.Store();
        store.Register(Registration());
        var host = new Host { PreparationResult = new(null, state, phase + " interrupted", "BelowNormal", "fixed",
            Execution(new(0, "case-000", "AA", 0, "B", true)).Resources, "stdout", "stderr") };
        var run = await ExperimentReplayRunner.RunAsync(store, Registration(), fixture.Root, host, default);
        Assert.Equal("incomplete", run.State);
        Assert.Equal(phase + " interrupted", run.Reason);
        var receipt = Assert.Single(store.Executions(run.Id));
        Assert.Equal(state, receipt.State);
        Assert.Equal(100, receipt.Resources!.CpuMilliseconds);
        Assert.Equal("BelowNormal", receipt.Priority);
        Assert.Contains(new ExperimentReplaySource("preparation-receipt", "hash"), receipt.Sources);
        Assert.Equal(0, host.MaximumConcurrent);
    }

    [Theory]
    [InlineData("Timeout")]
    [InlineData("Error")]
    [InlineData("Aborted")]
    [InlineData("NotRunnable")]
    [InlineData("Unknown")]
    public void Procedure_MixedFatalAndFailedRows_AreInvalid(string outcome)
    {
        using var fixture = new ExperimentReplayTestData();
        var path = Path.Combine(fixture.Root, "tests.trx");
        File.WriteAllText(path, $"<TestRun><UnitTestResult testName='Fails' outcome='Failed'/><UnitTestResult testName='Other' outcome='{outcome}'/></TestRun>");
        Assert.NotNull(ExperimentReplayEvidence.ExecutionError(ExperimentReplayEvidence.Tests(path), 2));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(-1, false)]
    [InlineData(137, false)]
    public void Procedure_FailedRows_RequireNativeMtpTestFailureExit(int exit, bool valid)
    {
        Assert.Equal(valid, ExperimentReplayEvidence.ExecutionError([new("Fails", "Failed")], exit) is null);
        Assert.Null(ExperimentReplayEvidence.ExecutionError([new("Passes", "Passed")], 0));
    }

    [Fact]
    public void Procedure_FatalSummaryWithPartialRows_IsInvalid()
    {
        using var fixture = new ExperimentReplayTestData();
        var path = Path.Combine(fixture.Root, "tests.trx");
        File.WriteAllText(path, "<TestRun><UnitTestResult testName='Fails' outcome='Failed'/><Counters failed='1' timeout='1'/></TestRun>");
        Assert.NotNull(ExperimentReplayEvidence.ExecutionError(ExperimentReplayEvidence.Tests(path), 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Procedure_UnownedLinkedWorktree_PreservesTrackedAndUntrackedFiles(bool replayLocation)
    {
        using var fixture = new CheckoutFixture(replayLocation);
        Assert.True(File.Exists(Path.Combine(fixture.Checkout, ".git")));
        Assert.Equal(fixture.Commit, Git(fixture.Checkout, "rev-parse", "HEAD"));
        File.WriteAllText(Path.Combine(fixture.Checkout, "tracked.txt"), "operator edit");
        File.WriteAllText(Path.Combine(fixture.Checkout, "untracked.txt"), "operator file");
        var request = new ExperimentReplayProcedure.Request(fixture.Checkout, fixture.Commit, fixture.Commit, "A",
            "unused-dotnet", Path.Combine(fixture.Root, "build"), Path.Combine(fixture.Root, "results"), Path.Combine(fixture.Root, "completion.json"),
            CheckoutRoot: fixture.CheckoutRoot);
        var requestPath = Path.Combine(fixture.Root, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, ExperimentReplayStore.Json));
        Assert.Equal(1, await ExperimentReplayProcedure.RunAsync(requestPath, default));
        var completion = JsonSerializer.Deserialize<ExperimentReplayProcedure.Completion>(File.ReadAllText(request.CompletionPath), ExperimentReplayStore.Json)!;
        Assert.Equal("apparatus-failed", completion.State);
        Assert.Equal("operator edit", File.ReadAllText(Path.Combine(fixture.Checkout, "tracked.txt")));
        Assert.Equal("operator file", File.ReadAllText(Path.Combine(fixture.Checkout, "untracked.txt")));
    }

    [Fact]
    public void CheckoutLease_RequiresActiveOwnerAndPinnedCandidate()
    {
        using var fixture = new CheckoutFixture(true);
        var lease = ExperimentReplayCheckoutLease.Create(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot);
        string staleReceipt;
        using (var read = new FileStream(fixture.Checkout + ".lease.json", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(read)) staleReceipt = reader.ReadToEnd();
        using (lease)
        {
            using var active = ExperimentReplayCheckoutLease.Acquire(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot);
            Assert.Throws<InvalidDataException>(() => ExperimentReplayCheckoutLease.Acquire(fixture.Checkout, Candidate, fixture.CheckoutRoot));
            Assert.Throws<InvalidDataException>(() => ExperimentReplayCheckoutLease.Acquire(fixture.Checkout, fixture.Commit, null));
        }
        File.WriteAllText(fixture.Checkout + ".lease.json", staleReceipt);
        try { Assert.Throws<InvalidDataException>(() => ExperimentReplayCheckoutLease.Acquire(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot)); }
        finally { File.Delete(fixture.Checkout + ".lease.json"); }
    }

    [Fact]
    public void CheckoutLease_AttachedHeadIsRejectedEvenWithLiveLease()
    {
        using var fixture = new CheckoutFixture(true);
        using var lease = ExperimentReplayCheckoutLease.Create(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot);
        Git(fixture.Checkout, "checkout", "--quiet", "-b", "attached-fixture");
        Assert.Equal(fixture.Commit, Git(fixture.Checkout, "rev-parse", "HEAD"));
        Assert.Throws<InvalidDataException>(() => ExperimentReplayCheckoutLease.Acquire(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot));
    }

    [Fact]
    public async Task Procedure_OwnedCheckout_ResetsAndPublishesFailureWithoutTestHost()
    {
        using var fixture = new CheckoutFixture(true);
        using var lease = ExperimentReplayCheckoutLease.Create(fixture.Checkout, fixture.Commit, fixture.CheckoutRoot);
        File.WriteAllText(Path.Combine(fixture.Checkout, "tracked.txt"), "procedure edit");
        File.WriteAllText(Path.Combine(fixture.Checkout, "untracked.txt"), "procedure file");
        var request = new ExperimentReplayProcedure.Request(fixture.Checkout, fixture.Commit, fixture.Commit, "A",
            "unused-dotnet", Path.Combine(fixture.Root, "build"), Path.Combine(fixture.Root, "results"), Path.Combine(fixture.Root, "completion.json"),
            CheckoutRoot: fixture.CheckoutRoot);
        var path = Path.Combine(fixture.Root, "request.json");
        File.WriteAllText(path, JsonSerializer.Serialize(request, ExperimentReplayStore.Json));
        Assert.Equal(1, await ExperimentReplayProcedure.RunAsync(path, default));
        var completion = JsonSerializer.Deserialize<ExperimentReplayProcedure.Completion>(File.ReadAllText(request.CompletionPath), ExperimentReplayStore.Json)!;
        Assert.Equal("apparatus-failed", completion.State);
        Assert.Equal("committed", File.ReadAllText(Path.Combine(fixture.Checkout, "tracked.txt")));
        Assert.False(File.Exists(Path.Combine(fixture.Checkout, "untracked.txt")));
        Assert.Equal(fixture.Commit, Git(fixture.Checkout, "rev-parse", "HEAD"));
    }

    private static string Git(string root, params string[] args)
    {
        var result = GitCli.RunForRepository(root, ["-c", "core.hooksPath=", "-c", "commit.gpgsign=false", .. args]);
        Assert.True(result.ProcessStarted && result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output.Trim();
    }

    private sealed class CheckoutFixture : IDisposable
    {
        private readonly ExperimentReplayTestData _data = new();
        internal string Root => _data.Root;
        internal string Checkout { get; }
        internal string CheckoutRoot { get; }
        internal string Commit { get; }
        internal CheckoutFixture(bool replayLocation)
        {
            Git(Root, "init", "--quiet");
            File.WriteAllText(Path.Combine(Root, "tracked.txt"), "committed");
            Git(Root, "add", "tracked.txt");
            Git(Root, "-c", "user.name=Replay Test", "-c", "user.email=replay@example.invalid", "-c", "core.hooksPath=", "commit", "--quiet", "-m", "fixture");
            Commit = Git(Root, "rev-parse", "HEAD");
            CheckoutRoot = OrchestratorTempRoot.GetPurposeDirectory("experiment-replay", Root);
            Checkout = replayLocation ? Path.Combine(CheckoutRoot, Guid.NewGuid().ToString("N"))
                : Path.Combine(Root, "goal-worktree");
            Directory.CreateDirectory(Path.GetDirectoryName(Checkout)!);
            Git(Root, "worktree", "add", "--quiet", "--detach", Checkout, Commit);
        }
        public void Dispose()
        {
            try { Git(Root, "worktree", "remove", "--force", Checkout); }
            finally { _data.Dispose(); }
        }
    }

    private sealed class Host : ExperimentReplayRunner.IHost
    {
        internal int Prepared, Cleaned, MaximumConcurrent;
        internal bool Invalid, Cancel;
        internal string ReferenceState = "passed";
        internal int ReferenceCalls;
        internal ExperimentReplayProcess.Result? PreparationResult;
        private int _active;
        public Task<ExperimentReplayRunner.Prepared> PrepareAsync(ExperimentReplayCase item, string artifacts, CancellationToken token)
        {
            Prepared++;
            if (PreparationResult is { } result)
                ExperimentReplayRunner.RequirePreparationSuccess(result, [new("preparation-receipt", "hash")], "preparation unavailable");
            return Task.FromResult(new ExperimentReplayRunner.Prepared(item.Id, artifacts));
        }
        public Task<ExperimentReplayExecution> ExecuteAsync(ExperimentReplayRegistration registration, ExperimentReplayRun run,
            ExperimentReplaySlot slot, ExperimentReplayRunner.Prepared prepared, string artifacts, CancellationToken token)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, ++_active);
            var execution = Execution(slot) with { RunId = run.Id, Resources = Invalid ? null : Execution(slot).Resources,
                State = Cancel ? "cancelled" : "passed" };
            if (slot.Stage == "reference")
            {
                ReferenceCalls++;
                execution = execution with { State = ReferenceState,
                    Reason = ReferenceState == "passed" ? null : "reference " + ReferenceState,
                    Sources = [new("reference-receipt", "hash")] };
            }
            _active--;
            return Task.FromResult(execution);
        }
        public void Cleanup(ExperimentReplayRunner.Prepared prepared) => Cleaned++;
    }
}
