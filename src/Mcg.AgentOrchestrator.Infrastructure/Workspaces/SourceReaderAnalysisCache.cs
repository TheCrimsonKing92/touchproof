using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Rebuildable whole-source analysis shared by selections, processes and worktrees for one user.
internal sealed class SourceReaderAnalysisCache(string? directory)
{
    // Bump for serialized shape OR analysis semantics changes in SourceReadingGuardTestClasses
    // or WholeTreeSourceReaderDetector.
    internal const int FormatVersion = 1;
    private static readonly Lazy<SourceReaderAnalysisCache> Shared = new(() => new(DefaultDirectory()));
    private static readonly AsyncLocal<SourceReaderAnalysisCache?> Scoped = new();
    private long _analysisCount;

    internal sealed record Analysis(bool DeclaresSnapshotCache, IReadOnlyList<string> ReaderOwners);

    internal static SourceReaderAnalysisCache Current => Scoped.Value ?? Shared.Value;
    internal long AnalysisCount => Interlocked.Read(ref _analysisCount);

    internal static IDisposable Use(SourceReaderAnalysisCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var previous = Scoped.Value;
        Scoped.Value = cache;
        return new Scope(() => Scoped.Value = previous);
    }

    internal static string Key(ImpactReplayTestSourceIndex sources)
    {
        var texts = sources.Trees.Select(tree => tree.GetText().ToString()).Order(StringComparer.Ordinal).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> frame = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(frame, texts.LongLength);
        hash.AppendData(frame);
        foreach (var text in texts)
        {
            BinaryPrimitives.WriteInt64LittleEndian(frame, (long)text.Length * sizeof(char));
            hash.AppendData(frame);
            // Encode exact UTF-16 code units, including unpaired surrogates, without replacement.
            var bytes = new byte[checked(text.Length * sizeof(char))];
            for (var index = 0; index < text.Length; index++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * sizeof(char)), text[index]);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal string? EntryPath(string key) => directory is null ? null : Path.Combine(directory, key + ".json");

    internal Analysis GetOrAnalyze(ImpactReplayTestSourceIndex sources,
        Func<ImpactReplayTestSourceIndex, Analysis> analyze)
    {
        var key = Key(sources);
        if (TryRead(key, out var analysis)) return analysis;
        Interlocked.Increment(ref _analysisCount);
        analysis = analyze(sources);
        Write(key, analysis);
        return analysis;
    }

    internal bool TryRead(string key, out Analysis analysis)
    {
        analysis = null!;
        try
        {
            if (EntryPath(key) is not { } path) return false;
            var entry = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path));
            if (entry is null || entry.FormatVersion != FormatVersion || entry.Key != key ||
                entry.ReaderOwners is null || entry.ReaderOwners.Any(string.IsNullOrWhiteSpace)) return false;
            analysis = new(entry.DeclaresSnapshotCache,
                entry.ReaderOwners.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void Write(string key, Analysis analysis)
    {
        string? temporary = null;
        try
        {
            if (EntryPath(key) is not { } destination) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(FormatVersion, key,
                analysis.ReaderOwners.ToArray(), analysis.DeclaresSnapshotCache)));
            // Publish a closed, complete entry by renaming within the destination filesystem.
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
        }
    }

    private static string? DefaultDirectory()
    {
        try
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(local))
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(local) ? null
                : Path.Combine(local, "Mcg.AgentOrchestrator", "source-reader-analysis");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private sealed record Envelope(
        [property: JsonRequired] int FormatVersion,
        [property: JsonRequired] string Key,
        [property: JsonRequired] string[]? ReaderOwners,
        [property: JsonRequired] bool DeclaresSnapshotCache);

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
