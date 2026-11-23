using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class GoalSnapshotByteAccounting : IDisposable
{
    private static readonly AsyncLocal<GoalSnapshotByteAccounting?> Current = new();
    private readonly GoalSnapshotByteAccounting? _previous = Current.Value;
    private readonly object _gate = new();
    private Direction _reads, _writes;
    private (long? Deserialized, long? Serialized)? _sealed;

    private GoalSnapshotByteAccounting() => Current.Value = this;

    internal static GoalSnapshotByteAccounting Begin() => new();

    internal (long? Deserialized, long? Serialized) Seal(bool pendingReadWork = false)
    {
        lock (_gate)
            return _sealed ??= (pendingReadWork ? null : _reads.Total, _writes.Total);
    }

    public void Dispose()
    {
        Seal();
        Current.Value = _previous;
    }

    internal static JsonDocument Parse(string json, long? utf8Bytes = null)
    {
        RecordRead(utf8Bytes);
        return JsonDocument.Parse(json);
    }

    internal static T? Deserialize<T>(string json, JsonSerializerOptions options, long? utf8Bytes = null)
    {
        RecordRead(utf8Bytes);
        return JsonSerializer.Deserialize<T>(json, options);
    }

    internal static T? Deserialize<T>(Stream stream, JsonSerializerOptions options)
    {
        using var measurement = BeginStreamRead();
        try { return JsonSerializer.Deserialize<T>(stream, options); }
        finally { CompleteStreamRead(measurement, stream); }
    }

    internal static async ValueTask<T?> DeserializeAsync<T>(
        Stream stream, JsonSerializerOptions options, CancellationToken token)
    {
        using var measurement = BeginStreamRead();
        try { return await JsonSerializer.DeserializeAsync<T>(stream, options, token); }
        finally { CompleteStreamRead(measurement, stream); }
    }

    internal static async Task<JsonDocument> ParseAsync(Stream stream, CancellationToken token)
    {
        using var measurement = BeginStreamRead();
        try { return await JsonDocument.ParseAsync(stream, cancellationToken: token); }
        finally { CompleteStreamRead(measurement, stream); }
    }

    internal static string Serialize<T>(T snapshot, JsonSerializerOptions options)
    {
        Current.Value?.Record(write: true, null);
        return JsonSerializer.Serialize(snapshot, options);
    }

    internal static JsonElement SerializeToElement<T>(T snapshot, JsonSerializerOptions options)
    {
        Current.Value?.Record(write: true, null);
        return JsonSerializer.SerializeToElement(snapshot, options);
    }

    internal static Measurement? PrepareWriteMeasurement() => Current.Value?.NewMeasurement(write: true);

    private static Measurement? BeginStreamRead()
    {
        var measurement = Current.Value?.NewMeasurement(write: false);
        measurement?.Start();
        return measurement;
    }

    private static void CompleteStreamRead(Measurement? measurement, Stream stream)
    {
        if (measurement is not null)
            measurement.Utf8Bytes = GoalSnapshotArchiveCodec.GetCompleteUtf8Bytes(stream);
    }

    // Scheduled work can still reach a repository after the receipt. Reserve its possible
    // directions at submission time and let any actual serializer calls record normally.
    internal static IDisposable? TrackDeferredSnapshotWork(bool maySerialize = true)
    {
        var scope = Current.Value;
        if (scope is null)
            return null;
        lock (scope._gate)
        {
            if (scope._sealed.HasValue)
                return null;
            scope._reads.Pending++;
            if (maySerialize)
                scope._writes.Pending++;
            return new DeferredWork(scope, maySerialize);
        }
    }

    private static void RecordRead(long? utf8Bytes) => Current.Value?.Record(write: false, utf8Bytes);

    private Measurement? NewMeasurement(bool write)
    {
        lock (_gate)
            return _sealed.HasValue ? null : new Measurement(this, write);
    }

    private void Record(bool write, long? utf8Bytes)
    {
        lock (_gate)
        {
            if (!_sealed.HasValue)
                DirectionFor(write).Add(utf8Bytes);
        }
    }

    private ref Direction DirectionFor(bool write) => ref (write ? ref _writes : ref _reads);

    internal sealed class Measurement(GoalSnapshotByteAccounting owner, bool write) : IDisposable
    {
        private bool _started;
        internal long? Utf8Bytes { get; set; }

        internal void Start()
        {
            lock (owner._gate)
            {
                if (owner._sealed.HasValue)
                    return;
                _started = true;
                owner.DirectionFor(write).Pending++;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (!_started || owner._sealed.HasValue)
                    return;
                _started = false;
                ref var direction = ref owner.DirectionFor(write);
                direction.Pending--;
                direction.Add(Utf8Bytes);
            }
        }
    }

    private sealed class DeferredWork(GoalSnapshotByteAccounting owner, bool maySerialize) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (owner._sealed.HasValue)
                    return;
                owner._reads.Pending--;
                if (maySerialize)
                    owner._writes.Pending--;
            }
        }
    }

    private struct Direction
    {
        private long _bytes;
        private bool _unknown;
        internal int Pending;
        internal readonly long? Total => _unknown || Pending != 0 ? null : _bytes;

        internal void Add(long? utf8Bytes)
        {
            if (utf8Bytes is { } bytes)
                _bytes += bytes;
            else
                _unknown = true;
        }
    }
}
