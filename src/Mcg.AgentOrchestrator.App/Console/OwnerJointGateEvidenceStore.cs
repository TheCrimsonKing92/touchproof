using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Owns observations and their source identities for one console lifetime, including absent evidence.
internal sealed class OwnerJointGateEvidenceStore(
    Func<OwnerConductEvent, OwnerJointGateEvidenceStore.Observation> observe, TimeProvider clock)
{
    private const int Capacity = 512;
    private static readonly TimeSpan ReceiptRefresh = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    private readonly Queue<Key> _oldest = new();

    internal OwnerActivityTestEvidence? Read(OwnerConductEvent item, OwnerActivityTestEvidence? recorded = null)
    {
        if (item.EventKind != "acceptance-cohort" || !string.IsNullOrWhiteSpace(recorded?.FirstFailure)) return recorded;
        var key = new Key(item.EventKind, item.GoalId, Field(item, "receipt"), Field(item, "identity"),
            Field(item, "attempt"), Field(item, "kind"));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_entries.TryGetValue(key, out var cached) && now >= cached.ObservedAt &&
                now - cached.ObservedAt < ReceiptRefresh && cached.Observation.Sources.Matches())
                return Compose(cached.Observation, recorded);
            var observation = observe(item);
            if (!_entries.ContainsKey(key))
            {
                if (_entries.Count == Capacity) _entries.Remove(_oldest.Dequeue());
                _oldest.Enqueue(key);
            }
            _entries[key] = new(observation, now);
            return Compose(observation, recorded);
        }
    }

    internal static OwnerActivityTestEvidence? Compose(Observation observation, OwnerActivityTestEvidence? recorded)
    {
        if (observation.Evidence is not { } evidence) return recorded;
        return new(recorded?.Tests.Count > 0 ? recorded.Tests : evidence.Tests,
            recorded?.Checks.Count > 0 ? recorded.Checks : evidence.Checks, recorded?.OwnTest ?? false, evidence.FirstFailure);
    }

    internal static TextReader OpenOutput(string path) => new StreamReader(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    internal sealed record Observation(OwnerActivityTestEvidence? Evidence, SourceSnapshot Sources);
    private sealed record Key(string EventKind, string? GoalId, string? Receipt,
        string? Identity, string? Attempt, string? Kind);
    private sealed record Entry(Observation Observation, DateTimeOffset ObservedAt);

    // Capture metadata before consumption so a concurrent source change cannot bless stale parsed data.
    internal sealed class SourceSnapshot
    {
        private readonly List<Stamp?> _files = [];
        private string[]? _trainFiles;
        internal string? TrainDirectory { get; set; }

        internal string Track(string path, string directory)
        {
            var fullPath = Path.GetFullPath(path, directory);
            _files.Add(Stamp.Read(fullPath));
            return fullPath;
        }

        internal string[] TrainFiles() => _trainFiles = Listing(TrainDirectory!);

        internal bool Matches()
        {
            try
            {
                return _files.All(stamp => stamp is not null && stamp == Stamp.Read(stamp.Path)) &&
                    (TrainDirectory is null || (_trainFiles is not null &&
                        _trainFiles.SequenceEqual(Listing(TrainDirectory), StringComparer.Ordinal)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }

        private static string[] Listing(string directory) => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.trx").Order(StringComparer.Ordinal).ToArray() : [];

        private sealed record Stamp(string Path, long Length, DateTime LastWriteTimeUtc)
        {
            internal static Stamp? Read(string path)
            {
                try
                {
                    var file = new FileInfo(path);
                    return file.Exists ? new(path, file.Length, file.LastWriteTimeUtc) : new(path, -1, default);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
            }
        }
    }
}
