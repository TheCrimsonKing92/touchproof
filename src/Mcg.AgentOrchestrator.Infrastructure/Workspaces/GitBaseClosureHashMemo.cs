using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Bounded process-lifetime hashes of immutable base closures.</summary>
internal sealed class GitBaseClosureHashMemo
{
    internal const int DefaultCapacity = 4096;
    internal static GitBaseClosureHashMemo Shared { get; } = new(DefaultCapacity);
    private static readonly Encoding KeyEncoding = new UTF8Encoding(false, true);
    private readonly object sync = new();
    private readonly int capacity;
    private readonly Dictionary<Key, LinkedListNode<Entry>> entries = new();
    private readonly LinkedList<Entry> recency = new();
    private long hits, misses;

    internal GitBaseClosureHashMemo(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
    }

    internal int Count { get { lock (sync) return entries.Count; } }
    internal long Hits => Interlocked.Read(ref hits);
    internal long Misses => Interlocked.Read(ref misses);
    internal readonly record struct Key(string BaseCommitId, string ChangedFilesDigest);
    private sealed record Entry(Key Key, string Hash);

    internal static bool TryCreateKey(string baseCommitId, IReadOnlyList<string> changedFiles, out Key key)
    {
        key = default;
        if (baseCommitId.Length is not (40 or 64) ||
            !baseCommitId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, changedFiles.Count);
            digest.AppendData(length);
            foreach (var path in changedFiles.OrderBy(path => path, StringComparer.Ordinal))
            {
                var bytes = KeyEncoding.GetBytes(path);
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                digest.AppendData(length);
                digest.AppendData(bytes);
            }
            key = new Key(baseCommitId, Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
            return true;
        }
        catch (EncoderFallbackException) { return false; }
    }

    internal bool TryGet(Key key, out string hash)
    {
        lock (sync)
        {
            if (entries.TryGetValue(key, out var node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                Interlocked.Increment(ref hits);
                hash = node.Value.Hash;
                return true;
            }
            Interlocked.Increment(ref misses);
            hash = string.Empty;
            return false;
        }
    }

    internal void Store(Key key, string hash)
    {
        lock (sync)
        {
            // Independent concurrent misses retain only one completed answer.
            if (entries.Remove(key, out var previous)) recency.Remove(previous);
            entries.Add(key, recency.AddFirst(new Entry(key, hash)));
            if (entries.Count > capacity)
            {
                entries.Remove(recency.Last!.Value.Key);
                recency.RemoveLast();
            }
        }
    }
}
