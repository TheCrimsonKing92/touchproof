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
    internal const int FormatVersion = 8;
    private static readonly Lazy<SourceReaderAnalysisCache> Shared = new(() => new(DefaultDirectory()));
    private static readonly AsyncLocal<SourceReaderAnalysisCache?> Scoped = new();
    private long _analysisCount;
    private long _semanticFileCount;

    internal sealed record Analysis(bool DeclaresSnapshotCache, IReadOnlyList<string> ReaderOwners);

    internal static SourceReaderAnalysisCache Current => Scoped.Value ?? Shared.Value;
    internal long AnalysisCount => Interlocked.Read(ref _analysisCount);
    internal long SemanticFileCount => Interlocked.Read(ref _semanticFileCount);

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
        Interlocked.Add(ref _semanticFileCount, sources.Trees.Count);
        analysis = analyze(sources);
        Write(key, analysis);
        return analysis;
    }

    internal Analysis GetOrAnalyze(ImpactReplayTestSourceIndex sources,
        Func<ImpactReplayTestSourceIndex, HashSet<string>?, SourceReaderFileFacts[]> extract,
        Func<ImpactReplayTestSourceIndex, SourceReaderFileFacts[], Analysis> evaluate)
    {
        var key = Key(sources);
        if (TryRead(key, out var hit)) return hit;
        Interlocked.Increment(ref _analysisCount);
        var candidate = sources.Trees.Select(tree => SourceReaderFileFacts.Describe(sources, tree)).ToArray();
        var baseline = FindBaseline(candidate);
        var plan = baseline is null ? null : SourceReaderFileFacts.Plan(baseline, candidate);
        SourceReaderFileFacts[] files;
        if (plan is null)
        {
            files = Full(); // Missing/invalid baseline, unbounded change, or unusable candidate paths.
        }
        else
        {
            Interlocked.Add(ref _semanticFileCount, plan.Count);
            var fresh = extract(sources, plan);
            var replacements = fresh.ToDictionary(file => file.Path, StringComparer.Ordinal);
            var previous = baseline!.ToDictionary(file => file.Path, StringComparer.Ordinal);
            // Plan includes every changed file and every reference to old or new changed names.
            // Only hash-equal, non-invalidated files reach the baseline branch below.
            files = candidate.Select(file => replacements.TryGetValue(file.Path, out var replacement)
                ? replacement : previous[file.Path]).ToArray();
            if (!SourceReaderFileFacts.Valid(files) || LostResolution(baseline!, fresh))
                files = Full(); // Dangling identities or lost bindings cannot be partially trusted.
        }
        var analysis = evaluate(sources, files);
        Write(key, analysis, files);
        return analysis;

        SourceReaderFileFacts[] Full()
        {
            // Count actual work, including an incremental attempt if it needed a full fallback.
            Interlocked.Add(ref _semanticFileCount, sources.Trees.Count);
            return extract(sources, null);
        }
    }

    private static bool LostResolution(SourceReaderFileFacts[] baseline, SourceReaderFileFacts[] fresh)
    {
        var old = baseline.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var file in fresh)
        {
            if (!old.TryGetValue(file.Path, out var before)) continue;
            var resolved = before.Members.SelectMany(member => member.InvocationTexts.Select((text, index) =>
                    (text, resolved: member.InvocationTargets[index].Length != 0)))
                .Where(call => call.resolved).Select(call => call.text).ToHashSet(StringComparer.Ordinal);
            if (file.Members.Any(member => member.InvocationTexts.Where((text, index) =>
                    resolved.Contains(text) && member.InvocationTargets[index].Length == 0).Any())) return true;
        }
        return false;
    }

    private SourceReaderFileFacts[]? FindBaseline(SourceReaderFileFacts[] candidate)
    {
        if (directory is null || !SourceReaderFileFacts.HasUniquePaths(candidate)) return null;
        try
        {
            var hashes = candidate.ToDictionary(file => file.Path, file => file.ContentHash, StringComparer.Ordinal);
            SourceReaderFileFacts[]? best = null;
            var bestOverlap = -1;
            // Bound JSON reads; newest wins ties. No store or caller changes are needed for baseline discovery.
            foreach (var path in new DirectoryInfo(directory).EnumerateFiles("*.json")
                         .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).Take(8))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path.FullName));
                    if (entry is null || entry.FormatVersion != FormatVersion || entry.Key != Path.GetFileNameWithoutExtension(path.Name) ||
                        entry.ReaderOwners is null || entry.ReaderOwners.Any(string.IsNullOrWhiteSpace) ||
                        entry.Files is null || entry.FilesDigest != Digest(entry.Key, entry.Files) ||
                        !SourceReaderFileFacts.Valid(entry.Files)) continue;
                    var overlap = entry.Files.Count(file => hashes.GetValueOrDefault(file.Path) == file.ContentHash);
                    if (overlap <= bestOverlap) continue;
                    best = entry.Files;
                    bestOverlap = overlap;
                }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            }
            return best;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static string Digest(string key, SourceReaderFileFacts[] files) =>
        SourceReaderFileFacts.Hash(key + JsonSerializer.Serialize(files));

    internal bool TryRead(string key, out Analysis analysis)
    {
        analysis = null!;
        try
        {
            if (EntryPath(key) is not { } path) return false;
            // Exact hits need only the result; System.Text.Json skips the potentially large facts payload.
            var entry = JsonSerializer.Deserialize<ResultEnvelope>(File.ReadAllText(path));
            if (entry is null || entry.FormatVersion != FormatVersion || entry.Key != key ||
                entry.ReaderOwners is null || entry.ReaderOwners.Any(string.IsNullOrWhiteSpace)) return false;
            analysis = new(entry.DeclaresSnapshotCache,
                entry.ReaderOwners.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void Write(string key, Analysis analysis, SourceReaderFileFacts[]? files = null)
    {
        string? temporary = null;
        try
        {
            if (EntryPath(key) is not { } destination) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(FormatVersion, key,
                analysis.ReaderOwners.ToArray(), analysis.DeclaresSnapshotCache, files,
                files is null ? null : Digest(key, files))));
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
        [property: JsonRequired] bool DeclaresSnapshotCache,
        SourceReaderFileFacts[]? Files,
        string? FilesDigest);

    private sealed record ResultEnvelope(
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
