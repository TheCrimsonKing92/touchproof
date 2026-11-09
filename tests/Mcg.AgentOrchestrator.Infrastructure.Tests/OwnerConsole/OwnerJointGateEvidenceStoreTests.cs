using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every fixture owns its temporary files, reader counters, cache and manual clock.
public sealed class OwnerJointGateEvidenceStoreTests
{
    [Fact]
    public void Read_UnchangedReceiptlessTrain_ParsesTrxAndOpensStdoutOnce()
    {
        using var fixture = new Fixture();
        fixture.Train();
        var first = Assert.IsType<OwnerActivityTestEvidence>(fixture.Read());
        var second = Assert.IsType<OwnerActivityTestEvidence>(fixture.Read());
        Assert.Equal(1, fixture.Observations);
        Assert.Equal(1, fixture.TrxReads);
        Assert.Equal(1, fixture.OutputOpens);
        Assert.Equal("TrainTests.First: first failure", first.FirstFailure);
        Assert.Equal(new[] { "Example.TrainTests.First" }, first.Tests);
        Assert.Equal(first.FirstFailure, second.FirstFailure);
        Assert.Equal<string>(first.Tests, second.Tests);
        Assert.Equal<string>(first.Checks, second.Checks);
    }

    [Theory]
    [InlineData("trx-length")]
    [InlineData("trx-time")]
    [InlineData("stdout")]
    [InlineData("verdict")]
    [InlineData("attempt")]
    public void Read_ChangedSource_ReobservesOnlyItsEvent(string change)
    {
        using var fixture = new Fixture();
        fixture.Train();
        fixture.Read();
        var unrelated = Event("kind=train identity=other");
        Assert.Null(fixture.Store.Read(unrelated));
        switch (change)
        {
            case "trx-length": fixture.Trx("00-first.trx", "a longer failure"); break;
            case "trx-time":
                var path = fixture.PathFor("merge-train-evidence/t1/00-first.trx");
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
                break;
            case "stdout": File.AppendAllText(fixture.PathFor("merge-train-evidence/t1/build.log"), "\nmore output"); break;
            case "verdict": fixture.Write("merge-train-evidence/t1/gate-verdict.json", """{"StdoutPath":"build.log","failedChecks":["build"]}"""); break;
            case "attempt": fixture.Write("grouped-gate-attempts/a1/a1.attempt.json", """{"failedChecks":["test"]}"""); break;
        }
        var updated = Assert.IsType<OwnerActivityTestEvidence>(fixture.Read());
        Assert.Null(fixture.Store.Read(unrelated));
        Assert.Equal(3, fixture.Observations);
        Assert.Equal(2, fixture.TrxReads);
        Assert.Equal(2, fixture.OutputOpens);
        Assert.Equal(change == "trx-length" ? "TrainTests.First: a longer failure" : "TrainTests.First: first failure",
            updated.FirstFailure);
    }

    [Fact]
    public void Read_NewTrx_ReenumeratesAndPreservesFirstOrdinalFailure()
    {
        using var fixture = new Fixture();
        fixture.Train();
        fixture.Read();
        fixture.Trx("01-later.trx", "later failure", "Example.LaterTests.Second");
        var result = Assert.IsType<OwnerActivityTestEvidence>(fixture.Read());
        Assert.Equal(2, fixture.Observations);
        Assert.Equal(3, fixture.TrxReads);
        Assert.Equal(2, fixture.OutputOpens);
        Assert.Equal("TrainTests.First: first failure", result.FirstFailure);
        Assert.Equal(new[] { "Example.TrainTests.First", "Example.LaterTests.Second" }, result.Tests);
    }

    [Fact]
    public void Read_NoFailingTest_ReusesNoFailureDetailObservation()
    {
        using var fixture = new Fixture();
        fixture.Write("merge-train-evidence/t1/00-passed.trx", "<TestRun><Results/></TestRun>");
        Assert.Equal("recorded gate evidence contains no failure detail", fixture.Read()?.FirstFailure);
        Assert.Equal("recorded gate evidence contains no failure detail", fixture.Read()?.FirstFailure);
        Assert.Equal(1, fixture.Observations);
        Assert.Equal(1, fixture.TrxReads);
    }

    [Fact]
    public void Read_NullObservation_PicksUpNewReceiptAtSixtySeconds()
    {
        using var fixture = new Fixture();
        fixture.Sql("""
            CREATE TABLE cohort_receipts(cohort_id TEXT, receipt_id TEXT, completed_at TEXT,
                gate_exit_code INTEGER);
            """);
        var item = Event("kind=cohort identity=c1");
        Assert.Null(fixture.Store.Read(item));
        fixture.Sql("INSERT INTO cohort_receipts VALUES('c1','r1','2026-11-09',1);");
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(fixture.Store.Read(item));
        Assert.Equal(1, fixture.Observations);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("gate exited 1", fixture.Store.Read(item)?.FirstFailure);
        Assert.Equal(2, fixture.Observations);
        Assert.Equal("gate exited 1", fixture.Store.Read(item)?.FirstFailure);
        Assert.Equal(2, fixture.Observations);
    }

    [Fact]
    public void Read_MissingVerdictAndAttempt_AppearanceInvalidatesNullObservation()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read());
        Assert.Null(fixture.Read());
        Assert.Equal(1, fixture.Observations);
        fixture.Write("grouped-gate-attempts/a1/a1.attempt.json", """{"StdoutPath":"build.log"}""");
        Assert.Null(fixture.Read());
        Assert.Equal(2, fixture.Observations);
        fixture.Write("merge-train-evidence/t1/gate-verdict.json", """{"gateExitCode":2}""");
        Assert.Equal("gate exited 2", fixture.Read()?.FirstFailure);
        Assert.Equal(3, fixture.Observations);
    }

    [Fact]
    public void Read_MissingOutput_AppearanceInvalidatesObservation()
    {
        using var fixture = new Fixture();
        fixture.Write("merge-train-evidence/t1/gate-verdict.json", """{"StdoutPath":"build.log"}""");
        Assert.Equal("recorded gate evidence contains no failure detail", fixture.Read()?.FirstFailure);
        fixture.Write("merge-train-evidence/t1/build.log", "File.cs(1,2): error CS0103: newly recorded error");
        Assert.Equal("File.cs(1,2): error CS0103: newly recorded error", fixture.Read()?.FirstFailure);
        Assert.Equal(2, fixture.Observations);
        Assert.Equal(1, fixture.OutputOpens);
    }

    [Fact]
    public void Read_RecordedListsChange_ComposesCurrentListsWithCachedFailure()
    {
        using var fixture = new Fixture();
        fixture.Train();
        fixture.Read();
        var recorded = new OwnerActivityTestEvidence(["current test"], ["current check"], true);
        var result = Assert.IsType<OwnerActivityTestEvidence>(fixture.Store.Read(Fixture.TrainEvent, recorded));
        Assert.Same(recorded.Tests, result.Tests);
        Assert.Same(recorded.Checks, result.Checks);
        Assert.True(result.OwnTest);
        Assert.Equal("TrainTests.First: first failure", result.FirstFailure);
        Assert.Equal(1, fixture.Observations);
    }

    [Fact]
    public void Read_RecordedFailureOrUnrelatedEvent_DoesNotObserve()
    {
        using var fixture = new Fixture();
        var recorded = new OwnerActivityTestEvidence([], [], false, "already explained");
        Assert.Same(recorded, fixture.Store.Read(Fixture.TrainEvent, recorded));
        Assert.Null(fixture.Store.Read(new(default, "other", null, "kind=train identity=t1")));
        Assert.Equal(0, fixture.Observations);
    }

    [Fact]
    public void Read_SourceChangesDuringParsing_DoesNotReuseStaleFailure()
    {
        using var fixture = new Fixture();
        fixture.Train();
        var reads = 0;
        var reader = new OwnerJointGateFailureEvidence(fixture.PathFor(""), path =>
        {
            var result = AcceptanceTrxFailureReader.Read(path);
            if (++reads == 1) fixture.Trx("00-first.trx", "updated failure");
            return result;
        });
        var store = new OwnerJointGateEvidenceStore(reader.Observe, fixture.Clock);
        Assert.Equal("TrainTests.First: first failure", store.Read(Fixture.TrainEvent)?.FirstFailure);
        Assert.Equal("TrainTests.First: updated failure", store.Read(Fixture.TrainEvent)?.FirstFailure);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Read_TrxDeleted_RemovesItsFailure()
    {
        using var fixture = new Fixture();
        fixture.Train();
        Assert.Equal("TrainTests.First: first failure", fixture.Read()?.FirstFailure);
        File.Delete(fixture.PathFor("merge-train-evidence/t1/00-first.trx"));
        var result = Assert.IsType<OwnerActivityTestEvidence>(fixture.Read());
        Assert.Equal("File.cs(1,2): error CS0103: unknown name", result.FirstFailure);
        Assert.Empty(result.Tests);
        Assert.Equal(2, fixture.Observations);
    }

    [Theory]
    [InlineData("other-goal", "kind=train identity=t1 attempt=a1")]
    [InlineData(null, "kind=train identity=t1 attempt=a1 receipt=r1")]
    [InlineData(null, "kind=train identity=t2 attempt=a1")]
    [InlineData(null, "kind=train identity=t1 attempt=a2")]
    [InlineData(null, "kind=cohort identity=t1 attempt=a1")]
    public void Read_DifferentIdentityFields_HaveIndependentEntries(string? goal, string fields)
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Read());
        Assert.Null(fixture.Store.Read(Event(fields) with { GoalId = goal }));
        Assert.Equal(2, fixture.Observations);
        Assert.Null(fixture.Read());
        Assert.Equal(2, fixture.Observations);
    }

    [Fact]
    public void Read_CapacityExceeded_EvictsOldestObservation()
    {
        var observations = 0;
        var store = new OwnerJointGateEvidenceStore(_ =>
        {
            observations++;
            return new(null, new());
        }, new ManualClock());
        for (var index = 0; index <= 512; index++) Assert.Null(store.Read(Event("identity=" + index)));
        Assert.Equal(513, observations);
        Assert.Null(store.Read(Event("identity=512")));
        Assert.Equal(513, observations);
        Assert.Null(store.Read(Event("identity=0")));
        Assert.Equal(514, observations);
    }

    private static OwnerConductEvent Event(string fields) => new(default, "acceptance-cohort", null, fields);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly OwnerConductEvent TrainEvent = Event("kind=train identity=t1 attempt=a1");
        private readonly string _root = SharedTestSupport.CreateTempDirectory();
        internal readonly ManualClock Clock = new();
        internal readonly OwnerJointGateEvidenceStore Store;
        internal int Observations, TrxReads, OutputOpens;

        internal Fixture()
        {
            var reader = new OwnerJointGateFailureEvidence(_root,
                path => { TrxReads++; return AcceptanceTrxFailureReader.Read(path); },
                path => { OutputOpens++; return OwnerJointGateEvidenceStore.OpenOutput(path); });
            Store = new(item => { Observations++; return reader.Observe(item); }, Clock);
        }

        internal OwnerActivityTestEvidence? Read() => Store.Read(TrainEvent);
        internal string PathFor(string path) => Path.Combine(_root, path);
        internal void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(path))!);
            File.WriteAllText(PathFor(path), text);
        }

        internal void Trx(string name, string message, string test = "Example.TrainTests.First") => Write(
            "merge-train-evidence/t1/" + name,
            $"<TestRun><Results><UnitTestResult testName=\"{test}\" outcome=\"Failed\"><Output><ErrorInfo><Message>{message}</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>");

        internal void Train()
        {
            Trx("00-first.trx", "first failure");
            Write("merge-train-evidence/t1/gate-verdict.json", """{"StdoutPath":"build.log"}""");
            Write("merge-train-evidence/t1/build.log", "File.cs(1,2): error CS0103: unknown name");
            Write("grouped-gate-attempts/a1/a1.attempt.json", "{}");
        }

        internal void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = PathFor("cohort-acceptance.db"), Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
    }
}
