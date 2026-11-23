using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using static BoardFillSnapshotTestDatabase;

public sealed class BoardFillSnapshotMeasurementTests(ITestOutputHelper output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private const int SeedItems = 2600;
    private const int TimedPairs = 5;
    private static readonly HashSet<string> NoExclusions = [];

    [Fact(Skip = "Requires the Windows SQLite VFS and per-thread CPU accounting.", SkipUnless = nameof(IsWindows))]
    public void Three_arms_report_cost_through_the_first_matching_scheduling_decision()
    {
        const string full = "current-full";
        const string historical = "integrated-49238cad";
        const string projection = "experimental-projection";
        string[] arms = [full, historical, projection];
        var quantum = ObserveCpuQuantum();
        output.WriteLine($"scheduling-comparison full=f122e81ebc7cfabc50e1c21ceef176474e4bb7ad " +
            $"historical=534b124943c796e126195fc31d70aba611ee5dda baseline_tree=28088178ff2b9bc564e45efc161381d055948a36 " +
            $"candidate_module_mvid={typeof(BoardFillSchedulingSnapshot).Module.ModuleVersionId} " +
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} " +
            $"arch={RuntimeInformation.ProcessArchitecture} processors={Environment.ProcessorCount} cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} " +
            $"configuration={typeof(BoardFillSchedulingSnapshot).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                .OfType<System.Reflection.AssemblyConfigurationAttribute>().SingleOrDefault()?.Configuration} " +
            $"stopwatch_hz={Stopwatch.Frequency} observed_thread_cpu_quantum_us={CpuMicroseconds(quantum)} " +
            "cache=warm-process-and-filesystem,non-pooled-sqlite,cold-new-reader " +
            "boundary=backlog-acquisition-through-ordered-decision-with-fixed-goals-rounds-epic-inputs " +
            "writer=common-revision-schema sql_payload=utf8-text-blob,8-byte-numeric,0-byte-null wal_growth=physical-file-length-delta");

        foreach (var (count, open) in new[] { (2618, 687), (2618, 1374) })
        {
            using var fixture = BoardFillSchedulingComparison.Create(count, open);
            ReportFixture(fixture);
            _ = MeasureSchedulingArm(fixture, full, "cold", trace: false);
            _ = MeasureSchedulingArm(fixture, historical, "quiet", trace: false);
            _ = MeasureSchedulingArm(fixture, projection, "changed", trace: false);

            foreach (var scenario in new[] { "cold", "quiet", "changed", "first-after-commit" })
            {
                var samples = arms.ToDictionary(arm => arm, _ => new List<SchedulingSample>());
                for (var pair = 0; pair < TimedPairs; pair++)
                {
                    var index = pair;
                    var pairOrder = Enumerable.Range(0, arms.Length)
                        .Select(offset => arms[(index + (index % 2 == 0 ? offset : arms.Length - offset)) % arms.Length]);
                    var sample = pairOrder.ToDictionary(arm => arm, arm => MeasureSchedulingArm(fixture, arm, scenario, trace: false));
                    foreach (var arm in arms)
                    {
                        Assert.Equal(sample[full].Decisions, sample[arm].Decisions);
                        samples[arm].Add(sample[arm]);
                    }
                }

                var traced = arms.ToDictionary(arm => arm, arm => MeasureSchedulingArm(fixture, arm, scenario, trace: true));
                foreach (var arm in arms)
                {
                    Assert.Equal(traced[full].Decisions, traced[arm].Decisions);
                    var counts = traced[arm];
                    output.WriteLine($"scheduling-cost open={open} scenario={scenario} arm={arm} repetitions={TimedPairs} " +
                        $"decisions={counts.Decisions.Length} read_statements={counts.ReadStatements} read_rows={counts.ReadRows} " +
                        $"read_payload_bytes={counts.ReadPayload} write_statements={counts.WriteStatements} " +
                        $"write_rows={counts.WriteRows} write_payload_bytes={counts.WritePayload} " +
                        $"trigger_steps={counts.TriggerSteps} {ReportTotals(samples[arm].Select(sample => sample.Totals))}");
                }
                Assert.True(traced[projection].ReadPayload < traced[full].ReadPayload);
                foreach (var baseline in new[] { full, historical })
                {
                    var deltas = samples[baseline].Zip(samples[projection], (a, b) => a.Totals - b.Totals);
                    var payloadSavings = traced[baseline].ReadPayload - traced[projection].ReadPayload;
                    output.WriteLine($"scheduling-savings open={open} scenario={scenario} baseline={baseline} " +
                        $"versus={projection} read_payload_bytes={payloadSavings} {ReportTotals(deltas)} " +
                        $"decision_cpu_resolution={CpuResolution(deltas.Select(delta => delta.DecisionCpu), quantum)}");
                }
            }
            ReportProjectionPlans(fixture);
        }
    }

    private void ReportFixture(BoardFillSnapshotTestDatabase fixture)
    {
        using var session = new SqliteReadSession(fixture.Path);
        using var command = session.Connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), (SELECT journal_mode FROM pragma_journal_mode), " +
            "(SELECT page_size FROM pragma_page_size), (SELECT version FROM store_schema_versions WHERE store_name='backlog')";
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            output.WriteLine($"scheduling-schema provider={typeof(SqliteConnection).Assembly.GetName().Version} " +
                $"sqlite={reader.GetString(0)} journal={reader.GetString(1)} page_bytes={reader.GetInt64(2)} backlog_version={reader.GetInt64(3)}");
        }
        command.CommandText = """
            SELECT name, coalesce(sql, '(implicit)') FROM sqlite_schema
            WHERE tbl_name IN ('backlog', 'backlog_notes', 'backlog_dependencies')
                AND type IN ('table', 'index') ORDER BY type, name
            """;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                output.WriteLine($"scheduling-schema-object name={reader.GetString(0)} sql={reader.GetString(1).ReplaceLineEndings(" ")}");
        }
        var snapshot = BoardFillBacklogSnapshot.Read(fixture.Path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Contents(snapshot))));
        foreach (var group in snapshot.GroupBy(item => item.Status))
            output.WriteLine($"scheduling-fixture id={digest} source_backlog=4447ad6b69eb4f2c9c083b05dc5c87cd status={group.Key} " +
                $"items={group.Count()} dependencies={group.Sum(item => item.Dependencies.Count)} " +
                $"notes={group.Sum(item => item.Notes.Count)} body_utf8_bytes={group.Sum(item => Encoding.UTF8.GetByteCount(item.Body))} " +
                $"note_utf8_bytes={group.Sum(item => item.Notes.Sum(note => Encoding.UTF8.GetByteCount(note.Text)))}");
    }

    private void ReportProjectionPlans(BoardFillSnapshotTestDatabase fixture)
    {
        using var planSession = new SqliteReadSession(fixture.Path);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _ = BoardFillSchedulingComparison.ReadProjection(fixture.Path, query: (kind, executed) =>
        {
            if (!seen.Add(kind)) return;
            using var explain = planSession.Connection.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + executed.CommandText;
            foreach (SqliteParameter parameter in executed.Parameters)
                explain.Parameters.AddWithValue(parameter.ParameterName, parameter.Value);
            using var reader = explain.ExecuteReader();
            var details = new List<string>();
            while (reader.Read()) details.Add(reader.GetString(3));
            Assert.NotEmpty(details);
            output.WriteLine($"scheduling-plan query={kind} bindings={executed.Parameters.Count} sql={executed.CommandText} plan={string.Join(" | ", details)}");
        });
        Assert.Equal(new[] { "candidate", "dependency", "historical", "note", "prerequisite" }, seen.Order(StringComparer.Ordinal));
    }

    private static SchedulingSample MeasureSchedulingArm(BoardFillSnapshotTestDatabase fixture, string arm, string scenario, bool trace)
    {
        var path = fixture.At("sample.db");
        File.Copy(fixture.Path, path);
        try
        {
            var readTrace = trace ? new BoardFillSnapshotSqlTrace(measureResultPayload: true) : null;
            var writeTrace = trace ? new BoardFillSnapshotSqlTrace(measureResultPayload: true) : null;
            Action<string, SqliteConnection>? observe = trace ? (source, connection) => readTrace!.Attach(source, connection) : null;
            using var cache = new BoardFillBacklogSnapshotCache(path, observe);
            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            writer.Open();
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA wal_autocheckpoint=0";
                setup.ExecuteNonQuery();
            }

            BoardFillSchedulingComparison.Decision Decide() => arm switch
            {
                "current-full" => BoardFillSchedulingComparison.DecideFull(BoardFillBacklogSnapshot.Read(path, observe)),
                "integrated-49238cad" => BoardFillSchedulingComparison.DecideFull(cache.Read()),
                "experimental-projection" => BoardFillSchedulingComparison.DecideProjected(
                    BoardFillSchedulingComparison.ReadProjection(path, observe)),
                _ => throw new ArgumentOutOfRangeException(nameof(arm))
            };

            if (scenario != "cold")
            {
                var baseline = Decide();
                Assert.Equal(BoardFillSchedulingComparison.Dependent, baseline.Selected);
                // Exclude cache priming from timed and traced counters.
                if (trace)
                    readTrace = new BoardFillSnapshotSqlTrace(measureResultPayload: true);
            }

            writeTrace?.Attach(path, writer);
            var steps = scenario switch
            {
                "cold" or "first-after-commit" => 1,
                "quiet" => 16,
                "changed" => 5,
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            var receipts = new string[steps];
            var totals = new SchedulingTotals();
            for (var step = 0; step < steps; step++)
            {
                long? committed = null;
                if (scenario is "changed" or "first-after-commit")
                {
                    var sql = SchedulingMutation(step);
                    var walBefore = WalLength(path);
                    var writeCpu = ThreadCpu();
                    var writeAlloc = GC.GetAllocatedBytesForCurrentThread();
                    var writeStarted = Stopwatch.GetTimestamp();
                    Execute(writer, sql);
                    committed = Stopwatch.GetTimestamp();
                    totals = totals with
                    {
                        WriteCpu = totals.WriteCpu + ThreadCpu() - writeCpu,
                        WriteAllocated = totals.WriteAllocated + GC.GetAllocatedBytesForCurrentThread() - writeAlloc,
                        WriteElapsed = totals.WriteElapsed + committed.Value - writeStarted,
                        WalGrowth = totals.WalGrowth + WalLength(path) - walBefore
                    };
                }

                var decisionCpu = ThreadCpu();
                var decisionAlloc = GC.GetAllocatedBytesForCurrentThread();
                var decisionStarted = Stopwatch.GetTimestamp();
                var decision = Decide();
                var decided = Stopwatch.GetTimestamp();
                totals = totals with
                {
                    DecisionCpu = totals.DecisionCpu + ThreadCpu() - decisionCpu,
                    DecisionAllocated = totals.DecisionAllocated + GC.GetAllocatedBytesForCurrentThread() - decisionAlloc,
                    DecisionElapsed = totals.DecisionElapsed + decided - decisionStarted,
                    CommitToDecision = totals.CommitToDecision + (committed is { } commitTime ? decided - commitTime : 0)
                };
                if (scenario is "changed" or "first-after-commit")
                    AssertChangedDecision(step, decision);
                receipts[step] = decision.Receipt();
            }

            Assert.Null(readTrace?.Failure);
            Assert.Null(writeTrace?.Failure);
            return new(totals, receipts, readTrace?.Statements ?? 0, readTrace?.Rows ?? 0,
                readTrace?.ResultPayloadBytes ?? 0, writeTrace?.Statements ?? 0, writeTrace?.Rows ?? 0,
                writeTrace?.ResultPayloadBytes ?? 0, writeTrace?.TriggerSteps ?? 0);
        }
        finally { File.Delete(path); }
    }

    private static long WalLength(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;

    private static string SchedulingMutation(int step) => step switch
    {
        0 => $"UPDATE backlog SET status='Superseded' WHERE id='{BoardFillSchedulingComparison.Prerequisite}'",
        1 => $"UPDATE backlog SET body='See src/Other.cs' WHERE id='{BoardFillSchedulingComparison.Historical}'",
        2 => $"UPDATE backlog_notes SET text='New evidence' WHERE backlog_item_id='{BoardFillSchedulingComparison.Held}'",
        3 => $"UPDATE backlog SET body='Unrelated archive edit' WHERE id='{BoardFillSchedulingComparison.Irrelevant}'",
        4 => $"UPDATE backlog SET tags='owner-gated' WHERE id='{BoardFillSchedulingComparison.Duplicate}'",
        _ => throw new ArgumentOutOfRangeException(nameof(step))
    };

    private static void AssertChangedDecision(int step, BoardFillSchedulingComparison.Decision decision) =>
        Assert.Equal(step switch
        {
            0 => BoardFillSchedulingComparison.Alternate,
            1 or 2 or 3 => BoardFillSchedulingComparison.Duplicate,
            4 => BoardFillSchedulingComparison.Held,
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        }, decision.Selected);

    private static string ReportTotals(IEnumerable<SchedulingTotals> samples)
    {
        var series = samples.ToArray();
        string Report(string name, Func<SchedulingTotals, long> metric, double scale = 1)
        {
            var values = series.Select(metric).Order().ToArray();
            return FormattableString.Invariant($"{name}_median_min_max={values[values.Length / 2] * scale:F1},{values[0] * scale:F1},{values[^1] * scale:F1}");
        }
        var elapsedScale = 1_000_000d / Stopwatch.Frequency;
        return string.Join(' ', Report("decision_cpu_us", sample => sample.DecisionCpu, 0.1),
            Report("decision_elapsed_us", sample => sample.DecisionElapsed, elapsedScale),
            Report("decision_allocated_bytes", sample => sample.DecisionAllocated),
            Report("writer_cpu_us", sample => sample.WriteCpu, 0.1),
            Report("writer_elapsed_us", sample => sample.WriteElapsed, elapsedScale),
            Report("writer_allocated_bytes", sample => sample.WriteAllocated),
            Report("writer_plus_decision_elapsed_us", sample => sample.WriteElapsed + sample.DecisionElapsed, elapsedScale),
            Report("commit_to_decision_elapsed_us", sample => sample.CommitToDecision, elapsedScale),
            Report("wal_growth_bytes", sample => sample.WalGrowth));
    }

    private readonly record struct SchedulingTotals(long DecisionCpu, long DecisionAllocated, long DecisionElapsed,
        long WriteCpu, long WriteAllocated, long WriteElapsed, long WalGrowth, long CommitToDecision)
    {
        public static SchedulingTotals operator -(SchedulingTotals a, SchedulingTotals b) =>
            new(a.DecisionCpu - b.DecisionCpu, a.DecisionAllocated - b.DecisionAllocated,
                a.DecisionElapsed - b.DecisionElapsed, a.WriteCpu - b.WriteCpu, a.WriteAllocated - b.WriteAllocated,
                a.WriteElapsed - b.WriteElapsed, a.WalGrowth - b.WalGrowth, a.CommitToDecision - b.CommitToDecision);
    }

    private sealed record SchedulingSample(SchedulingTotals Totals, string[] Decisions, int ReadStatements, int ReadRows,
        long ReadPayload, int WriteStatements, int WriteRows, long WritePayload, int TriggerSteps);

    [Fact]
    public void Sql_trace_counts_result_payload_by_storage_class()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        var trace = new BoardFillSnapshotSqlTrace(measureResultPayload: true);
        trace.Attach(":memory:", connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT char(233), X'0001', 4, 1.5, NULL
            UNION ALL SELECT 'a', X'', NULL, NULL, char(25991, 23383)
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.False(reader.Read());

        Assert.Null(trace.Failure);
        Assert.Equal(2, trace.Rows);
        Assert.Equal(27L, trace.ResultPayloadBytes); // 2 + 2 + 8 + 8 on row one, 1 + 6 on row two.
    }

    [Fact(Skip = "Requires the Windows SQLite VFS and per-thread CPU accounting.", SkipUnless = nameof(IsWindows))]
    public void Bounded_windows_report_paired_costs_and_freshness()
    {
        // JIT both arms and the probe before collecting bounded measurements.
        _ = MeasureWindow(["cold", "hit", "item", "note", "dependency", "fallback", "recovery"], trace: false);
        var windows = new (string Name, string[] Steps)[]
        {
            ("cold", Repeat(12, "cold")),
            ("unchanged", Repeat(128, "hit")),
            ("changed", Repeat(8, "item", "note", "dependency")),
            ("mixed", ["cold", .. Repeat(4, "hit", "hit", "hit", "item", "note", "dependency"),
                "fallback", "recovery", "hit"])
        };
        var cpuQuantum = ObserveCpuQuantum();
        output.WriteLine($"snapshot-metrics method=paired deterministic windows items={SeedItems} notes={SeedItems} " +
            $"dependencies={SeedItems / 2} cpu_us=calling-thread kernel+user GetThreadTimes " +
            $"cpu_unit_ns=100 observed_cpu_quantum_us={CpuMicroseconds(cpuQuantum)} timed_pairs={TimedPairs} " +
            $"runtime={RuntimeInformation.FrameworkDescription} warmup_pairs=1 " +
            "allocated_bytes=GC.GetAllocatedBytesForCurrentThread sql_rows=SQLite trace ROW events " +
            "sql_exec=SQLite trace STMT top-level events trigger_steps=SQLite trace trigger subprogram events " +
            "sql_result_payload_bytes=sum of SQLite trace ROW columns using sqlite3_column_bytes for UTF-8 TEXT and BLOB, " +
            "8 bytes per INTEGER or REAL, 0 per NULL. Payload excludes terminators, framing and database page bytes. " +
            "timing_and_trace=separate identical runs managed_retained_bytes=forced-GC heap estimate per host " +
            "from eight independently populated caches on each window's final database " +
            "full_read_retained_bytes=0 by lifetime writer=deterministic SQL mutations plus production note setup and coverage repair " +
            "baseline_setup=shared setup with schema marked newer to skip proof work history=workload sizing only");
        foreach (var (name, steps) in windows)
        {
            var started = DateTimeOffset.UtcNow;
            var samples = Enumerable.Range(0, TimedPairs)
                .Select(i => MeasureWindow(steps, trace: false, candidateFirst: i % 2 != 0)).ToArray();
            var timed = samples.OrderBy(pair => (pair.Full - pair.Reuse).TotalCpu).ElementAt(TimedPairs / 2);
            var counted = MeasureWindow(steps, trace: true);
            Assert.All(samples, sample => Assert.Equal(counted.Mix, sample.Mix));
            Assert.True(counted.Full.ReadResultPayloadBytes > 0);
            Assert.True(counted.Reuse.ReadResultPayloadBytes > 0);
            if (name == "unchanged")
            {
                Assert.Equal(steps.Length, counted.Mix.Hit);
                Assert.True(counted.Reuse.ReadRows < counted.Full.ReadRows);
                Assert.True(counted.Reuse.ReadStatements < counted.Full.ReadStatements);
            }
            if (name == "changed")
            {
                Assert.True(counted.Reuse.TriggerSteps > counted.Full.TriggerSteps);
                Assert.True(counted.Full.SetupStatements > 0);
                Assert.True(counted.Reuse.SetupStatements > counted.Full.SetupStatements);
            }
            if (name == "mixed")
                Assert.Equal(1, counted.Reuse.CoverageRepairs);

            var full = timed.Full.WithTrace(counted.Full);
            var reuse = timed.Reuse.WithTrace(counted.Reuse);
            var net = samples.Select(pair => pair.Full - pair.Reuse).ToArray();
            output.WriteLine($"snapshot-window name={name} started_utc={started:O} ended_utc={DateTimeOffset.UtcNow:O} " +
                $"ticks_per_pair={steps.Length} starting_items={SeedItems} pair_summary=median-net-cpu pair_order=alternating " +
                $"cold={timed.Mix.Cold} hit={timed.Mix.Hit} changed={timed.Mix.Changed} fallback={timed.Mix.Fallback} " +
                $"item_commits={steps.Count(x => x == "item")} note_commits={steps.Count(x => x is "note" or "fallback")} " +
                $"dependency_commits={steps.Count(x => x == "dependency")} coverage_repairs={counted.Reuse.CoverageRepairs} " +
                "retries=0 freshness=matched raw_and_selected_action=matched");
            output.WriteLine($"full {full.Report()} {CpuSpread(samples.Select(pair => pair.Full))} managed_retained_bytes=0");
            output.WriteLine($"reuse {reuse.Report()} {CpuSpread(samples.Select(pair => pair.Reuse))} " +
                $"managed_retained_bytes={counted.RetainedBytes}");
            output.WriteLine($"net_full_minus_reuse {(full - reuse).Report()} {CpuSpread(net)} " +
                $"managed_retained_bytes={-counted.RetainedBytes} " +
                $"snapshot_cpu_net_resolution={CpuResolution(net.Select(pair => pair.ReadCpu), cpuQuantum)} " +
                $"total_cpu_net_resolution={CpuResolution(net.Select(pair => pair.TotalCpu), cpuQuantum)}");
        }
    }

    private static string[] Repeat(int times, params string[] pattern) =>
        Enumerable.Range(0, times).SelectMany(_ => pattern).ToArray();

    private static (Costs Full, Costs Reuse, Mix Mix, long RetainedBytes) MeasureWindow(
        string[] steps, bool trace, bool candidateFirst = false)
    {
        using var baseline = new Arm(reuse: false, trace);
        using var candidate = new Arm(reuse: true, trace);
        var (first, second) = candidateFirst ? (candidate, baseline) : (baseline, candidate);
        IReadOnlyList<BacklogItem>? retained = null;
        if (steps[0] is not ("cold" or "fallback"))
        {
            baseline.Read();
            retained = candidate.Read();
            baseline.ResetMetrics();
            candidate.ResetMetrics();
        }

        var cold = 0;
        var hits = 0;
        var changes = 0;
        var fallbacks = 0;
        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            switch (step)
            {
                case "cold": retained = null; break;
                case "fallback": candidate.Write($"DROP TRIGGER {NoteTrigger}"); fallbacks++; break;
                case "recovery": candidate.RepairCoverage(); break;
            }
            if (step is "item" or "note" or "dependency" or "fallback")
            {
                var sql = Mutation(step == "fallback" ? "note" : step, index);
                first.Write(sql, setup: step == "note");
                second.Write(sql, setup: step == "note");
            }

            var firstRead = first.Read(restart: step == "cold");
            var secondRead = second.Read(restart: step == "cold");
            var (full, reuse) = candidateFirst ? (secondRead, firstRead) : (firstRead, secondRead);
            var hit = ReferenceEquals(retained, reuse);
            Assert.Equal(step == "hit", hit);
            if (hit) hits++;
            else if (step == "fallback") retained = null;
            else
            {
                if (retained is null) cold++;
                else changes++;
                retained = reuse;
            }
            // Row-level equality at each changed boundary and the end of the unchanged window.
            if (!hit || index == steps.Length - 1)
                Assert.Equal(Contents(full), Contents(reuse));
            Assert.Equal(Select(full), Select(reuse));
        }
        Assert.Null(baseline.TraceFailure);
        Assert.Null(candidate.TraceFailure);
        Assert.Equal(steps.Length, cold + hits + changes + fallbacks);
        var retainedBytes = trace && retained is not null ? MeasureRetainedBytes(candidate.Database.Path) : 0;
        return (baseline.Costs, candidate.Costs, new(cold, hits, changes, fallbacks, retained is not null), retainedBytes);

        static string? Select(IReadOnlyList<BacklogItem> snapshot) =>
            BoardFillReadyItemSelector.Select(snapshot, [], NoExclusions, item =>
                new(item.Dependencies.All(dependency => dependency.PrerequisiteId == "prerequisite"), false, ""))?.Id;
    }

    private static long MeasureRetainedBytes(string endStatePath)
    {
        var caches = Enumerable.Range(0, 8).Select(_ => new BoardFillBacklogSnapshotCache(endStatePath)).ToArray();
        try
        {
            var empty = GC.GetTotalMemory(forceFullCollection: true);
            foreach (var cache in caches) Populate(cache);
            var populated = GC.GetTotalMemory(forceFullCollection: true);
            foreach (var cache in caches) cache.Dispose();
            var released = GC.GetTotalMemory(forceFullCollection: true);
            // Both empty controls include cache objects. The post-dispose control
            // reduces drift from once-per-process initialization or unrelated GC work.
            var bytes = (populated - Math.Max(empty, released)) / caches.Length;
            Assert.True(bytes > 0, $"Expected live cache heap growth. Empty={empty}, populated={populated}, released={released}.");
            return bytes;
        }
        finally { foreach (var cache in caches) cache.Dispose(); }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Populate(BoardFillBacklogSnapshotCache cache)
        {
            var snapshot = cache.Read();
            Assert.Equal(SeedItems, snapshot.Count);
            Assert.Same(snapshot, cache.Read());
        }
    }

    private sealed class Arm : IDisposable
    {
        private readonly bool _reuse;
        private readonly bool _trace;
        private BoardFillSnapshotSqlTrace? _readerTrace;
        private BoardFillSnapshotSqlTrace? _writerTrace;
        private BoardFillSnapshotSqlTrace? _setupTrace;
        private BoardFillBacklogSnapshotCache? _cache;
        private Costs _timed;
        internal BoardFillSnapshotTestDatabase Database { get; } = new(SeedItems);
        internal Exception? TraceFailure => _readerTrace?.Failure ?? _writerTrace?.Failure ?? _setupTrace?.Failure;
        internal Costs Costs => _timed with
        {
            ReadRows = _readerTrace?.Rows ?? 0,
            ReadStatements = _readerTrace?.Statements ?? 0,
            ReadResultPayloadBytes = _readerTrace?.ResultPayloadBytes ?? 0,
            WriteRows = (_writerTrace?.Rows ?? 0) + (_setupTrace?.Rows ?? 0),
            WriteStatements = (_writerTrace?.Statements ?? 0) + (_setupTrace?.Statements ?? 0),
            WriteResultPayloadBytes = (_writerTrace?.ResultPayloadBytes ?? 0) + (_setupTrace?.ResultPayloadBytes ?? 0),
            TriggerSteps = (_writerTrace?.TriggerSteps ?? 0) + (_setupTrace?.TriggerSteps ?? 0),
            SetupStatements = _setupTrace?.Statements ?? 0,
            CoverageRepairs = _setupTrace?.ImmediateTransactions ?? 0
        };

        internal Arm(bool reuse, bool trace)
        {
            _reuse = reuse;
            _trace = trace;
            if (!reuse)
            {
                // Recreate the pre-proof writer shape after seeding. Setup's newer-schema
                // short circuit runs the same SQL as the old current-schema short circuit.
                using var connection = Database.Open();
                RemoveSnapshotProof(connection);
                Execute(connection, $"UPDATE store_schema_versions SET version = {StoreSchemaRegistry.Backlog.CurrentVersion + 1} WHERE store_name = 'backlog'");
            }
            ResetMetrics();
        }

        internal void ResetMetrics()
        {
            _readerTrace = _trace ? new(measureResultPayload: true) : null;
            _writerTrace = _trace ? new(measureResultPayload: true) : null;
            _setupTrace = _trace ? new(measureResultPayload: true) : null;
            _timed = default;
        }

        private void ObserveRead(string path, Microsoft.Data.Sqlite.SqliteConnection connection) =>
            _readerTrace!.Attach(path, connection);

        internal IReadOnlyList<BacklogItem> Read(bool restart = false)
        {
            var cpu = _trace ? 0 : ThreadCpu();
            var bytes = _trace ? 0 : GC.GetAllocatedBytesForCurrentThread();
            if (_reuse && restart)
            {
                _cache?.Dispose();
                _cache = null;
            }
            var result = _reuse
                ? (_cache ??= new(Database.Path, _trace ? ObserveRead : null)).Read()
                : BoardFillBacklogSnapshot.Read(Database.Path, _trace ? ObserveRead : null);
            if (!_trace)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                _timed.ReadCpu += ThreadCpu() - cpu;
                _timed.ReadAllocated += allocated;
            }
            return result;
        }

        internal void Write(string sql, bool setup = false) => MeasureWrite(() =>
        {
            if (setup)
                SetupWriter();
            using var connection = Database.Open();
            _writerTrace?.Attach(Database.Path, connection);
            Execute(connection, sql);
        });

        internal void RepairCoverage() => MeasureWrite(SetupWriter);

        private void SetupWriter() => BacklogStore.Setup(Database.Path, _trace ? ObserveSetup : null);

        private void ObserveSetup(Microsoft.Data.Sqlite.SqliteConnection connection) =>
            _setupTrace!.Attach(Database.Path, connection);

        private void MeasureWrite(Action write)
        {
            var cpu = _trace ? 0 : ThreadCpu();
            var bytes = _trace ? 0 : GC.GetAllocatedBytesForCurrentThread();
            write();
            if (!_trace)
            {
                var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                _timed.WriteCpu += ThreadCpu() - cpu;
                _timed.WriteAllocated += allocated;
            }
        }

        public void Dispose()
        {
            _cache?.Dispose();
            Database.Dispose();
        }
    }

    private record struct Mix(int Cold, int Hit, int Changed, int Fallback, bool PopulatedAtEnd);

    private record struct Costs(long ReadCpu, long WriteCpu, long ReadAllocated, long WriteAllocated,
        int ReadStatements, int WriteStatements, int ReadRows, int WriteRows, int TriggerSteps,
        int SetupStatements, int CoverageRepairs, long ReadResultPayloadBytes, long WriteResultPayloadBytes)
    {
        internal long TotalCpu => ReadCpu + WriteCpu;

        internal Costs WithTrace(Costs counted) => this with
        {
            ReadStatements = counted.ReadStatements, WriteStatements = counted.WriteStatements,
            ReadRows = counted.ReadRows, WriteRows = counted.WriteRows, TriggerSteps = counted.TriggerSteps,
            SetupStatements = counted.SetupStatements, CoverageRepairs = counted.CoverageRepairs,
            ReadResultPayloadBytes = counted.ReadResultPayloadBytes, WriteResultPayloadBytes = counted.WriteResultPayloadBytes
        };

        public static Costs operator -(Costs full, Costs reuse) => new(full.ReadCpu - reuse.ReadCpu,
            full.WriteCpu - reuse.WriteCpu, full.ReadAllocated - reuse.ReadAllocated,
            full.WriteAllocated - reuse.WriteAllocated, full.ReadStatements - reuse.ReadStatements,
            full.WriteStatements - reuse.WriteStatements, full.ReadRows - reuse.ReadRows,
            full.WriteRows - reuse.WriteRows, full.TriggerSteps - reuse.TriggerSteps,
            full.SetupStatements - reuse.SetupStatements, full.CoverageRepairs - reuse.CoverageRepairs,
            full.ReadResultPayloadBytes - reuse.ReadResultPayloadBytes,
            full.WriteResultPayloadBytes - reuse.WriteResultPayloadBytes);

        internal string Report() => FormattableString.Invariant(
            $"snapshot_cpu_us={ReadCpu / 10.0:F1} writer_cpu_us={WriteCpu / 10.0:F1} total_cpu_us={TotalCpu / 10.0:F1} snapshot_allocated_bytes={ReadAllocated} writer_allocated_bytes={WriteAllocated} snapshot_sql_exec={ReadStatements} writer_sql_exec={WriteStatements} writer_setup_sql_exec={SetupStatements} snapshot_sql_rows={ReadRows} writer_sql_rows={WriteRows} writer_trigger_steps={TriggerSteps} snapshot_sql_result_payload_bytes={ReadResultPayloadBytes} writer_sql_result_payload_bytes={WriteResultPayloadBytes}");
    }

    private static string CpuSpread(IEnumerable<Costs> samples)
    {
        var costs = samples.ToArray();
        return $"snapshot_cpu_us_min_max={Range(costs.Select(cost => cost.ReadCpu))} " +
            $"writer_cpu_us_min_max={Range(costs.Select(cost => cost.WriteCpu))} " +
            $"total_cpu_us_min_max={Range(costs.Select(cost => cost.TotalCpu))}";

        static string Range(IEnumerable<long> values) => $"{CpuMicroseconds(values.Min())},{CpuMicroseconds(values.Max())}";
    }

    private static string CpuResolution(IEnumerable<long> samples, long quantum)
    {
        if (quantum == 0)
            return "inconclusive-no-quantum-observed";

        return samples.All(sample => sample > quantum) || samples.All(sample => sample < -quantum)
            ? "same-direction-beyond-observed-quantum"
            : "inconclusive-clock-or-spread";
    }

    private static long ObserveCpuQuantum()
    {
        var minimum = long.MaxValue;
        var previous = ThreadCpu();
        for (var i = 0; i < 256; i++)
        {
            Thread.SpinWait(20_000);
            var current = ThreadCpu();
            if (current > previous)
                minimum = Math.Min(minimum, current - previous);
            previous = current;
        }
        return minimum == long.MaxValue ? 0 : minimum;
    }

    private static string CpuMicroseconds(long ticks) => FormattableString.Invariant($"{ticks / 10.0:F1}");

    private static long ThreadCpu() => GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
        ? kernel + user : throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
