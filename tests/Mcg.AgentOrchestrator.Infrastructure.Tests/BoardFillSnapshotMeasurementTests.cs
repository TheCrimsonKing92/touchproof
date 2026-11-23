using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static BoardFillSnapshotTestDatabase;

public sealed class BoardFillSnapshotMeasurementTests(ITestOutputHelper output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    private const int SeedItems = 2600;
    private static readonly HashSet<string> NoExclusions = [];

    [Fact(Skip = "Requires the Windows SQLite VFS and per-thread CPU accounting.", SkipUnless = nameof(IsWindows))]
    public void Bounded_windows_report_paired_costs_and_freshness()
    {
        // JIT both arms and the probe before collecting bounded measurements.
        _ = MeasureWindow(["cold", "hit", "item", "fallback", "recovery"], trace: false);
        var windows = new (string Name, string[] Steps)[]
        {
            ("cold", Repeat(12, "cold")),
            ("unchanged", Repeat(128, "hit")),
            ("changed", Repeat(8, "item", "note", "dependency")),
            ("mixed", ["cold", .. Repeat(4, "hit", "hit", "hit", "item", "note", "dependency"),
                "fallback", "recovery", "hit"])
        };
        var retained = MeasureRetainedBytes();
        output.WriteLine($"snapshot-metrics method=paired deterministic windows items={SeedItems} notes={SeedItems} " +
            $"dependencies={SeedItems / 2} cpu_us=calling-thread kernel+user GetThreadTimes " +
            "allocated_bytes=GC.GetAllocatedBytesForCurrentThread sql_rows=SQLite trace ROW events " +
            "sql_exec=SQLite trace STMT top-level events trigger_steps=SQLite trace trigger subprogram events " +
            "timing_and_trace=separate identical runs managed_retained_bytes=forced-GC heap delta per warmed host " +
            "full_read_retained_bytes=0 by lifetime writer=independent single-row SQLite statements " +
            "writer_cost_includes_trigger_and_injected_coverage_changes history=workload sizing only");
        foreach (var (name, steps) in windows)
        {
            var timed = MeasureWindow(steps, trace: false);
            var counted = MeasureWindow(steps, trace: true);
            Assert.Equal(timed.Mix, counted.Mix);
            if (name == "unchanged")
            {
                Assert.Equal(steps.Length, counted.Mix.Hit);
                Assert.True(counted.Reuse.ReadRows < counted.Full.ReadRows);
                Assert.True(counted.Reuse.ReadStatements < counted.Full.ReadStatements);
            }
            if (name == "changed")
                Assert.True(counted.Reuse.TriggerSteps > counted.Full.TriggerSteps);

            var full = timed.Full.WithTrace(counted.Full);
            var reuse = timed.Reuse.WithTrace(counted.Reuse);
            var endRetained = timed.Mix.PopulatedAtEnd ? retained : 0;
            output.WriteLine($"snapshot-window name={name} ticks={steps.Length} starting_items={SeedItems} " +
                $"cold={timed.Mix.Cold} hit={timed.Mix.Hit} changed={timed.Mix.Changed} fallback={timed.Mix.Fallback} " +
                $"item_commits={steps.Count(x => x == "item")} note_commits={steps.Count(x => x is "note" or "fallback")} " +
                $"dependency_commits={steps.Count(x => x == "dependency")} coverage_repairs={steps.Count(x => x == "recovery")} " +
                "retries=0 freshness=matched raw_and_selected_action=matched");
            output.WriteLine($"full {full.Report()} managed_retained_bytes=0");
            output.WriteLine($"reuse {reuse.Report()} managed_retained_bytes={endRetained}");
            output.WriteLine($"net_full_minus_reuse {(full - reuse).Report()} managed_retained_bytes={-endRetained}");
        }
    }

    private static string[] Repeat(int times, params string[] pattern) =>
        Enumerable.Range(0, times).SelectMany(_ => pattern).ToArray();

    private static (Costs Full, Costs Reuse, Mix Mix) MeasureWindow(string[] steps, bool trace)
    {
        using var baseline = new Arm(reuse: false, trace);
        using var candidate = new Arm(reuse: true, trace);
        string restore;
        using (var connection = candidate.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT sql FROM sqlite_schema WHERE name = '{NoteTrigger}'";
            restore = Assert.IsType<string>(command.ExecuteScalar());
        }
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
                case "recovery": candidate.Write(restore); break;
            }
            if (step is "item" or "note" or "dependency" or "fallback")
            {
                var sql = Mutation(step == "fallback" ? "note" : step, index);
                baseline.Write(sql);
                candidate.Write(sql);
            }

            var full = baseline.Read();
            var reuse = candidate.Read(restart: step == "cold");
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
        Assert.NotNull(retained);
        return (baseline.Costs, candidate.Costs, new(cold, hits, changes, fallbacks, retained is not null));

        static string? Select(IReadOnlyList<BacklogItem> snapshot) =>
            BoardFillReadyItemSelector.Select(snapshot, [], NoExclusions, item =>
                new(item.Dependencies.All(dependency => dependency.PrerequisiteId == "prerequisite"), false, ""))?.Id;
    }

    private static long MeasureRetainedBytes()
    {
        using var database = new BoardFillSnapshotTestDatabase(SeedItems);
        var caches = Enumerable.Range(0, 8).Select(_ => new BoardFillBacklogSnapshotCache(database.Path)).ToArray();
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
        static void Populate(BoardFillBacklogSnapshotCache cache) => Assert.Equal(SeedItems, cache.Read().Count);
    }

    private sealed class Arm : IDisposable
    {
        private readonly bool _reuse;
        private readonly bool _trace;
        private BoardFillSnapshotSqlTrace? _readerTrace;
        private BoardFillSnapshotSqlTrace? _writerTrace;
        private BoardFillBacklogSnapshotCache? _cache;
        private Costs _timed;
        internal BoardFillSnapshotTestDatabase Database { get; } = new(SeedItems);
        internal Exception? TraceFailure => _readerTrace?.Failure ?? _writerTrace?.Failure;
        internal Costs Costs => _timed with
        {
            ReadRows = _readerTrace?.Rows ?? 0,
            ReadStatements = _readerTrace?.Statements ?? 0,
            WriteRows = _writerTrace?.Rows ?? 0,
            WriteStatements = _writerTrace?.Statements ?? 0,
            TriggerSteps = _writerTrace?.TriggerSteps ?? 0
        };

        internal Arm(bool reuse, bool trace)
        {
            _reuse = reuse;
            _trace = trace;
            if (!reuse)
            {
                // Restore the v3 writer shape after seeding. Both arms issue the
                // same logical data updates, paying the candidate trigger cost.
                using var connection = Database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'trigger'";
                var triggers = new List<string>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) triggers.Add(reader.GetString(0));
                foreach (var trigger in triggers) Execute(connection, $"DROP TRIGGER {trigger}");
                Execute(connection, "DROP TABLE backlog_snapshot_revision; UPDATE store_schema_versions SET version = 3 WHERE store_name = 'backlog';");
            }
            ResetMetrics();
        }

        internal void ResetMetrics()
        {
            _readerTrace = _trace ? new() : null;
            _writerTrace = _trace ? new() : null;
            _timed = default;
        }

        private void ObserveRead(string path, Microsoft.Data.Sqlite.SqliteConnection connection) =>
            _readerTrace!.Attach(path, connection);

        internal IReadOnlyList<BacklogItem> Read(bool restart = false)
        {
            var cpu = _trace ? 0 : ThreadCpu();
            var bytes = _trace ? 0 : GC.GetAllocatedBytesForCurrentThread();
            if (restart)
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

        internal void Write(string sql)
        {
            var cpu = _trace ? 0 : ThreadCpu();
            var bytes = _trace ? 0 : GC.GetAllocatedBytesForCurrentThread();
            using (var connection = Database.Open())
            {
                _writerTrace?.Attach(Database.Path, connection);
                Execute(connection, sql);
            }
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
        int ReadStatements, int WriteStatements, int ReadRows, int WriteRows, int TriggerSteps)
    {
        internal Costs WithTrace(Costs counted) => this with
        {
            ReadStatements = counted.ReadStatements, WriteStatements = counted.WriteStatements,
            ReadRows = counted.ReadRows, WriteRows = counted.WriteRows, TriggerSteps = counted.TriggerSteps
        };

        public static Costs operator -(Costs full, Costs reuse) => new(full.ReadCpu - reuse.ReadCpu,
            full.WriteCpu - reuse.WriteCpu, full.ReadAllocated - reuse.ReadAllocated,
            full.WriteAllocated - reuse.WriteAllocated, full.ReadStatements - reuse.ReadStatements,
            full.WriteStatements - reuse.WriteStatements, full.ReadRows - reuse.ReadRows,
            full.WriteRows - reuse.WriteRows, full.TriggerSteps - reuse.TriggerSteps);

        internal string Report() => FormattableString.Invariant(
            $"snapshot_cpu_us={ReadCpu / 10.0:F1} writer_cpu_us={WriteCpu / 10.0:F1} total_cpu_us={(ReadCpu + WriteCpu) / 10.0:F1} snapshot_allocated_bytes={ReadAllocated} writer_allocated_bytes={WriteAllocated} snapshot_sql_exec={ReadStatements} writer_sql_exec={WriteStatements} snapshot_sql_rows={ReadRows} writer_sql_rows={WriteRows} writer_trigger_steps={TriggerSteps}");
    }

    private static long ThreadCpu() => GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user)
        ? kernel + user : throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
