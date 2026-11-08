using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One conductor-owned cursor shared by all executors. History is scanned once;
// subsequent ticks process appended bytes and retain only the comparison horizon.
internal sealed class ExecutorSlotSampleReader
{
    private readonly List<RemoteExecutorOutcomeRow> _rows = [];
    private readonly HashSet<string> _inferred = new(StringComparer.Ordinal);
    private long _offset;
    private byte[] _prefix = [];
    private string? _path;
    private DateTimeOffset? _retainedSince;
    internal long LastReadBytes { get; private set; }
    internal int RetainedRows => _rows.Count;

    internal static IReadOnlyList<ExecutorSlotSample> Read(string healthPath, string executorId,
        DateTimeOffset since, DateTimeOffset now) => new ExecutorSlotSampleReader().ReadRecent(healthPath, executorId, since, now);

    internal IReadOnlyList<ExecutorSlotSample> ReadRecent(string healthPath, string executorId,
        DateTimeOffset since, DateTimeOffset now)
    {
        LastReadBytes = 0;
        if (!File.Exists(healthPath))
        {
            Reset();
            return [];
        }
        using var stream = new FileStream(healthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var prefix = new byte[Math.Min(256L, stream.Length)];
        stream.ReadExactly(prefix);
        if (_path != healthPath || stream.Length < _offset || since < _retainedSince ||
            prefix.Length < _prefix.Length || !prefix.AsSpan(0, Math.Min(prefix.Length, _prefix.Length)).SequenceEqual(_prefix))
            Reset();
        _path = healthPath;
        if (_prefix.Length == 0) _prefix = prefix;
        stream.Position = _offset;
        using var content = new MemoryStream();
        stream.CopyTo(content);
        var bytes = content.ToArray();
        LastReadBytes = bytes.LongLength;
        var committedLength = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var text = Encoding.UTF8.GetString(bytes, 0, committedLength);
        if (_offset == 0) text = text.TrimStart('\uFEFF');
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line) Parse(line);
        _offset += committedLength; // An incomplete append is retried on the next tick.

        // Preserve earlier intervals that overlap the start of a selected attempt.
        var earliest = _rows.Where(row => row.ObservedAt >= since && row.ObservedAt <= now && row.StartedAt < row.ObservedAt)
            .Select(row => row.StartedAt!.Value).DefaultIfEmpty(since).Min();
        if (_retainedSince is { } retained && earliest < retained)
        {
            // A late terminal row can reveal an earlier interval than the retained horizon.
            // Reload rather than underestimate concurrency from previously evicted outcomes.
            Reset();
            return ReadRecent(healthPath, executorId, since, now);
        }
        var cutoff = earliest < since ? earliest : since;
        _rows.RemoveAll(row => row.ObservedAt < cutoff);
        _retainedSince = cutoff;
        _inferred.IntersectWith(_rows.Select(row => row.GateAttemptId));
        return ExecutorSlotConcurrencyAttribution.Build(_rows, executorId, since, now)
            .Select(sample => _inferred.Contains(sample.AttemptId) ? sample with { IntervalSource = "remote-duration" } : sample).ToArray();
    }

    private void Reset()
    {
        _offset = 0;
        _prefix = [];
        _rows.Clear();
        _inferred.Clear();
        _retainedSince = null;
    }

    private void Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var executor = root.GetProperty("executor_id").GetString();
            if (string.IsNullOrWhiteSpace(executor)) return;
            var observed = root.GetProperty("observed_at").GetDateTimeOffset();
            var id = root.GetProperty("gate_attempt_id").GetString()!;
            var outcome = root.GetProperty("outcome").GetString()!;
            if (outcome is "not-eligible-exclusive-resource" or "late-after-fallback" or "executor-busy" or
                "preempted-by-gate" or "executor-tools-mismatch") return;
            double? seconds = null;
            DateTimeOffset? started = null;
            var hasAttempt = root.TryGetProperty("attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Object;
            var focused = !hasAttempt && (root.TryGetProperty("goal_id", out var goal) && goal.ValueKind != JsonValueKind.Null ||
                root.TryGetProperty("evidence_shadow", out var shadow) && shadow.ValueKind != JsonValueKind.Null);
            if (hasAttempt)
            {
                if (attempt.TryGetProperty("last_status", out var status) && status.ValueKind == JsonValueKind.Object &&
                    status.TryGetProperty("seconds", out var duration) && duration.ValueKind == JsonValueKind.Number &&
                    duration.TryGetDouble(out var value)) seconds = value;
                if (attempt.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    foreach (var step in steps.EnumerateArray())
                        if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("started_at", out var start) &&
                            start.ValueKind == JsonValueKind.String && start.TryGetDateTimeOffset(out var at) &&
                            (started is null || at < started)) started = at;
            }
            if (focused && root.TryGetProperty("remote_duration_seconds", out var remote) &&
                remote.ValueKind == JsonValueKind.Number && remote.TryGetDouble(out var remoteSeconds) &&
                remoteSeconds > 0 && double.IsFinite(remoteSeconds))
            {
                seconds = remoteSeconds;
                try { started = observed.AddSeconds(-remoteSeconds); _inferred.Add(id); }
                catch (ArgumentOutOfRangeException) { started = null; }
            }
            _rows.Add(new(observed, executor, id, focused ? "focused-evidence" : root.GetProperty("lane").GetString()!,
                outcome, null, seconds, started));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
    }

    internal static ExecutorSlotControllerSettings ReadSettings(string path)
    {
        var settings = new ExecutorSlotControllerSettings();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("executorSlotController", out var policy))
                settings = policy.Deserialize<ExecutorSlotControllerSettings>(new JsonSerializerOptions
                    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling =
                        System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                    ?? throw new InvalidOperationException("invalid-executor-slot-controller-settings");
        }
        settings.Validate();
        return settings;
    }
}
