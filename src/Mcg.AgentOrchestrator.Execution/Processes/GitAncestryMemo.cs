namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Bounded process-lifetime answers about ordered immutable commit pairs.</summary>
internal sealed class GitAncestryMemo
{
    private static readonly StringComparer DirectoryComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static GitAncestryMemo Shared { get; } = new(4096);

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Func<string, string[], GitCli.GitResult> _run;
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = new(new KeyComparer());
    private readonly LinkedList<Entry> _recency = new();
    private long _hits;
    private long _misses;
    private static long _resolverAnswers;
    private static long _resolverDeclines;

    internal GitAncestryMemo(int capacity, Func<string, string[], GitCli.GitResult>? runner = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _run = runner ?? GitCli.RunForRepository;
    }

    internal long Hits => Interlocked.Read(ref _hits);
    internal long Misses => Interlocked.Read(ref _misses);

    internal GitCli.GitResult IsAncestor(string repositoryCommonDirectory, string ancestorId,
        string descendantId, string workingDirectory)
    {
        string? common = null;
        var eligible = IsFullId(ancestorId) && IsFullId(descendantId) &&
            TryCommonDirectory(repositoryCommonDirectory, out common);
        // The key's directory is meaningful only after the eligibility proof succeeded.
        var key = eligible ? new Key(common!, ancestorId, descendantId) : default;
        if (eligible)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var node))
                {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    Interlocked.Increment(ref _hits);
                    return node.Value.Result;
                }
            }
        }

        Interlocked.Increment(ref _misses);
        var result = _run(workingDirectory, ["merge-base", "--is-ancestor", ancestorId, descendantId]);
        if (eligible && result.ProcessStarted && !result.DrainTimedOut &&
            (result.ExitCode == 0 || result.ExitCode == 1 && result.Error.Length == 0))
        {
            lock (_sync)
            {
                // Concurrent misses may run independently; keep a single completed entry.
                if (_entries.Remove(key, out var previous)) _recency.Remove(previous);
                _entries.Add(key, _recency.AddFirst(new Entry(key, result)));
                if (_entries.Count > _capacity)
                {
                    var oldest = _recency.Last!;
                    _entries.Remove(oldest.Value.Key);
                    _recency.RemoveLast();
                }
            }
        }
        return result;
    }

    internal static void RecordResolverResult(bool resolved)
    {
        if (resolved) Interlocked.Increment(ref _resolverAnswers);
        else Interlocked.Increment(ref _resolverDeclines);
    }

    internal static string FormatSummary(int processId) => FormattableString.Invariant(
        $"LAUNCH_LOCK_AVOIDANCE pid={processId} ancestry_memo_hits={Shared.Hits} ancestry_memo_misses={Shared.Misses} ref_resolver_answers={Interlocked.Read(ref _resolverAnswers)} ref_resolver_declines={Interlocked.Read(ref _resolverDeclines)}");

    private static bool IsFullId(string id) => id.Length is 40 or 64 &&
        id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryCommonDirectory(string directory, out string? common)
    {
        common = null;
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return false;
            common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            // These repositories do not have immutable ancestry, even for full object ids.
            return !Path.Exists(Path.Combine(common, "shallow")) &&
                !Path.Exists(Path.Combine(common, "info", "grafts")) &&
                !Path.Exists(Path.Combine(common, "refs", "replace")) &&
                Environment.GetEnvironmentVariable("GIT_REPLACE_REF_BASE") is null &&
                Environment.GetEnvironmentVariable("GIT_NO_REPLACE_OBJECTS") is null &&
                Environment.GetEnvironmentVariable("GIT_GRAFT_FILE") is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private readonly record struct Key(string CommonDirectory, string Ancestor, string Descendant);
    private sealed record Entry(Key Key, GitCli.GitResult Result);
    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public bool Equals(Key x, Key y) => DirectoryComparer.Equals(x.CommonDirectory, y.CommonDirectory) &&
            x.Ancestor == y.Ancestor && x.Descendant == y.Descendant;
        public int GetHashCode(Key key) => HashCode.Combine(DirectoryComparer.GetHashCode(key.CommonDirectory),
            StringComparer.Ordinal.GetHashCode(key.Ancestor), StringComparer.Ordinal.GetHashCode(key.Descendant));
    }
}
