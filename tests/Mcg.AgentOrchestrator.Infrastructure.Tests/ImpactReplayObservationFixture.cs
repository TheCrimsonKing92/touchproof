using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;

// Private roots, SQLite connections, fake clock, and writers; no live stores or process-global mutations.
internal sealed class ImpactReplayObservationFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "impact-replay-observation-" + Guid.NewGuid().ToString("N"));
    internal string Destination => Path.Combine(Root, "reports");
    internal string Checkpoint => Path.Combine(Destination, ImpactReplayCheckpointStore.FileName);
    internal ManualClock Clock { get; } = new();
    internal FlushedWriter Error { get; } = new();
    internal StringWriter Output { get; } = new();
    internal FakeSource Source { get; } = new();

    internal ImpactReplayObservationFixture() => Directory.CreateDirectory(Root);

    internal int Run(CancellationToken token = default, int limit = 100) => CliImpactReplayCommand.Run(
        ["impact-replay", "--limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), "--out", Destination],
        Root, Root, null, Output, Error, token, Clock, Source);

    internal ImpactReplayRunSink Sink() => new(Destination,
        new(Guid.NewGuid().ToString("N"), Clock.GetUtcNow(),
            new(Root, Root, null, 100, null, 5, null), "fake runtime", "fake implementation", "fake revision"), Error, Clock);

    internal ImpactReplayCheckpointStatus Status() => Assert.Single(ImpactReplayCheckpointStore.ReadStatus(Checkpoint));

    internal ImpactReplayRunProgress[] Events() => Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith('{')).Select(line => JsonSerializer.Deserialize<ImpactReplayRunProgress>(line,
            ImpactReplayCheckpointStore.Json)!).ToArray();

    internal object? Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Checkpoint, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    internal static ImpactReplayStoredAttempt Attempt(string id) =>
        new(id, "goal-" + id, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), new('a', 40), new('b', 40), "unused", [], []);

    internal static ImpactReplayCaseResult Replayed(string id, string selection = "Resolved") => new(
        ImpactReplayCaseKind.Replayable,
        new(id, "goal-" + id, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), new('a', 40), new('b', 40),
            selection, null, null, [], [], [], [], [], [], 0, [], [], true, []));

    public void Dispose()
    {
        Output.Dispose();
        Error.Dispose();
        Directory.Delete(Root, true);
    }

    internal sealed class FlushedWriter : StringWriter
    {
        internal string Flushed { get; private set; } = "";
        public override void Flush() => Flushed = ToString();
    }

    internal sealed class FakeSource : ImpactReplayExecutionSource
    {
        internal ImpactReplayStoredAttempt[] Attempts { get; set; } = [Attempt("first"), Attempt("second")];
        internal Action? BeforeHistory { get; set; }
        internal Action? BeforeInventory { get; set; }
        internal Action<ICollection<ImpactReplayDiagnostic>>? LoadingDiagnostics { get; set; }
        internal Func<ImpactReplayStoredAttempt, ImpactReplayCaseContext, IImpactReplaySink, CancellationToken, ImpactReplayCaseResult>? OnCase { get; set; }
        internal Func<ImpactReplayCaseContext, string?>? OnCleanup { get; set; }
        internal int Cleaned { get; private set; }

        internal override string ReadSourceRevision(string executionDirectory) => "fake revision";
        internal override IReadOnlyDictionary<string, GateStatsReport.FlakeRow> LoadHistory(string directory,
            ICollection<ImpactReplayDiagnostic> skipped)
        {
            BeforeHistory?.Invoke();
            LoadingDiagnostics?.Invoke(skipped);
            return new Dictionary<string, GateStatsReport.FlakeRow>();
        }
        internal override IReadOnlyList<AcceptanceLaneFlakeRow> LoadLedger(string directory) => [];
        internal override IReadOnlyList<ImpactReplayStoredAttempt> LoadAttempts(string directory, DateTimeOffset? since,
            ICollection<ImpactReplayDiagnostic> skipped, IImpactReplaySink sink, CancellationToken cancellationToken)
        {
            BeforeInventory?.Invoke();
            sink.InventoryDiscovered(Attempts.Length);
            foreach (var _ in Attempts) sink.InputExamined();
            return Attempts;
        }
        internal override ImpactReplayCaseResult ExecuteCase(ImpactReplayStoredAttempt attempt,
            ImpactReplayRunConfiguration configuration, ImpactReplayCaseContext context,
            IReadOnlyList<AcceptanceLaneFlakeRow> flakes, IReadOnlyDictionary<string, GateStatsReport.FlakeRow> history,
            IImpactReplaySink sink, CancellationToken token)
        {
            sink.Phase(ImpactReplayPhase.Checkout);
            context.Checkout = Path.Combine(configuration.ExecutionDirectory, attempt.AttemptId);
            sink.OwnCheckout(context.Checkout);
            sink.Phase(ImpactReplayPhase.SourceAnalysis);
            sink.Phase(ImpactReplayPhase.EvidenceReading);
            sink.Phase(ImpactReplayPhase.ImpactAnalysis);
            return OnCase?.Invoke(attempt, context, sink, token) ?? Replayed(attempt.AttemptId);
        }
        internal override string? Cleanup(string directory, ImpactReplayCaseContext context)
        {
            Cleaned++;
            return OnCleanup?.Invoke(context);
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-09T00:00:00Z").AddTicks(_ticks);
        internal void Advance(int seconds)
        {
            _ticks += TimeSpan.FromSeconds(seconds).Ticks;
            _timer?.Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new ManualTimer(() => callback(state));

        private sealed class ManualTimer(Action callback) : ITimer
        {
            private bool _disposed;
            internal void Fire() { if (!_disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
