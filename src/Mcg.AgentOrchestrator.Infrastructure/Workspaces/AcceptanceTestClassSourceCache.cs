namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns file-local extracts for a bounded set of worktrees, never resolved cross-file membership.
internal sealed class AcceptanceTestClassSourceCache
{
    // Retain enough active goal trees to avoid evicting each goal before the next conductor tick.
    internal const int MaxWorktrees = 16;
    private static readonly AcceptanceTestClassSourceCache Shared = new();
    private static readonly AsyncLocal<AcceptanceTestClassSourceCache?> Scoped = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, WorktreeEntry> _worktrees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _gates = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();
    private readonly Func<string, AcceptanceTestClassSourceExtractStore?> _resolveStore;
    private long _parsedFileCount;

    internal AcceptanceTestClassSourceCache() => _resolveStore = AcceptanceTestClassSourceExtractStore.Resolve;

    // A null directory disables persistence for isolated first-level cache tests.
    internal AcceptanceTestClassSourceCache(string? storeDirectory) => _resolveStore =
        _ => storeDirectory is null ? null : new AcceptanceTestClassSourceExtractStore(storeDirectory);

    internal static AcceptanceTestClassSourceCache Current => Scoped.Value ?? Shared;

    internal static IDisposable Use(AcceptanceTestClassSourceCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var previous = Scoped.Value;
        Scoped.Value = cache;
        return new Scope(() => Scoped.Value = previous);
    }

    internal long ParsedFileCount => Interlocked.Read(ref _parsedFileCount);

    internal IReadOnlyList<AcceptanceTestClassSourceScanner.FileExtract> Extract(
        string worktreePath, IReadOnlyList<string> paths,
        Func<string, AcceptanceTestClassSourceScanner.FileExtract>? extractFile = null)
    {
        var root = Path.GetFullPath(worktreePath);
        object gate;
        lock (_sync)
        {
            if (!_gates.TryGetValue(root, out gate!)) _gates[root] = gate = new object();
        }
        lock (gate)
        {
            // The path callback is a first-level cache test seam; production extracts source text.
            var store = extractFile is null ? _resolveStore(root) : null;
            WorktreeEntry? previous;
            lock (_sync) _worktrees.TryGetValue(root, out previous);
            var parsed = new CachedFile[paths.Count];
            var errors = new System.Runtime.ExceptionServices.ExceptionDispatchInfo?[paths.Count];
            // Cold scans dominate Developer preflight. Parse independent files concurrently,
            // but retain enumeration order for cross-file resolution and deterministic errors.
            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            {
                try { parsed[index] = ReadFile(paths[index], previous, store, extractFile); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    errors[index] = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                }
            });
            foreach (var error in errors) error?.Throw();
            var files = new Dictionary<string, CachedFile>(StringComparer.Ordinal);
            var extracts = new AcceptanceTestClassSourceScanner.FileExtract[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                var path = Path.GetFullPath(paths[index]);
                var cached = parsed[index];
                files.Add(path, cached);
                extracts[index] = cached.Extract;
            }

            lock (_sync)
            {
                // Publish exactly the enumerated paths, dropping removed files even on cache hits.
                // Forget or another root's eviction may have removed the entry during parsing.
                if (_worktrees.TryGetValue(root, out var published)) _recency.Remove(published.Recency);
                _worktrees[root] = new WorktreeEntry(files, _recency.AddLast(root));
                while (_worktrees.Count > MaxWorktrees)
                {
                    _gates.Remove(_recency.First!.Value);
                    _worktrees.Remove(_recency.First!.Value);
                    _recency.RemoveFirst();
                }
            }
            return extracts;
        }
    }

    private CachedFile ReadFile(string inputPath, WorktreeEntry? previous,
        AcceptanceTestClassSourceExtractStore? store,
        Func<string, AcceptanceTestClassSourceScanner.FileExtract>? extractFile)
    {
        var path = Path.GetFullPath(inputPath);
        // Stat before reading: never associate old contents with a later write's metadata.
        var info = new FileInfo(path);
        var length = info.Length;
        var lastWriteUtc = info.LastWriteTimeUtc;
        CachedFile? cached = null;
        previous?.Files.TryGetValue(path, out cached);
        if (cached is not null && cached.Length == length && cached.LastWriteUtc == lastWriteUtc)
            return cached;
        var parses = cached?.Parses ?? 0;
        AcceptanceTestClassSourceScanner.FileExtract extract;
        if (extractFile is not null)
            extract = extractFile(path);
        else
        {
            var text = File.ReadAllText(path);
            var hash = store is null ? null : AcceptanceTestClassSourceExtractStore.HashText(text);
            if (hash is not null && store!.TryRead(hash, out extract))
                return new CachedFile(length, lastWriteUtc, extract, parses);
            // Hash and parse the same decoded string, even if the file changes during extraction.
            extract = AcceptanceTestClassSourceScanner.ExtractText(text);
            if (hash is not null) store!.Write(hash, extract);
        }
        Interlocked.Increment(ref _parsedFileCount);
        return new CachedFile(length, lastWriteUtc, extract, parses + 1);
    }

    internal void Forget(string worktreePath)
    {
        lock (_sync)
        {
            var root = Path.GetFullPath(worktreePath);
            _gates.Remove(root);
            if (_worktrees.Remove(root, out var entry))
                _recency.Remove(entry.Recency);
        }
    }

    internal int ParseCountForTests(string worktreePath, string path)
    {
        lock (_sync)
            return _worktrees.TryGetValue(Path.GetFullPath(worktreePath), out var entry) &&
                entry.Files.TryGetValue(Path.GetFullPath(path), out var file) ? file.Parses : 0;
    }

    internal IReadOnlyCollection<string> CachedPathsForTests(string worktreePath)
    {
        lock (_sync)
            return _worktrees.TryGetValue(Path.GetFullPath(worktreePath), out var entry)
                ? entry.Files.Keys.ToArray() : [];
    }

    internal AcceptanceTestClassSourceScanner.FileExtract ExtractForTests(string worktreePath, string path)
    {
        lock (_sync)
            return _worktrees[Path.GetFullPath(worktreePath)].Files[Path.GetFullPath(path)].Extract;
    }

    private sealed record CachedFile(long Length, DateTime LastWriteUtc,
        AcceptanceTestClassSourceScanner.FileExtract Extract, int Parses);
    private sealed record WorktreeEntry(Dictionary<string, CachedFile> Files, LinkedListNode<string> Recency);

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
        private Action? _restore = restore;
    }
}
