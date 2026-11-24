using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Rebuildable whole-source analysis shared by selections, processes and worktrees for one user.
internal sealed class SourceReaderAnalysisCache(string? directory)
{
    // Bump for serialized shape OR analysis semantics changes in SourceReadingGuardTestClasses
    // or WholeTreeSourceReaderDetector.
    internal const int FormatVersion = 10;
    private const int RetainedGenerations = 8;
    private const string PublicationLeaseFileName = ".publication.lock";
    private static string? _defaultDirectoryOverride;
    private static readonly Lazy<SourceReaderAnalysisCache> Shared = new(() => new(_defaultDirectoryOverride ?? DefaultDirectory()));
    private static readonly AsyncLocal<SourceReaderAnalysisCache?> Scoped = new();
    private long _analysisCount;
    private long _semanticFileCount;

    internal sealed record Analysis(bool DeclaresSnapshotCache, IReadOnlyList<string> ReaderOwners)
    {
        internal IReadOnlyList<AssemblyScanningGuardTestClasses.ScanFile> AssemblyScans { get; init; } = [];
    }

    internal static SourceReaderAnalysisCache Current => Scoped.Value ?? Shared.Value;
    internal long AnalysisCount => Interlocked.Read(ref _analysisCount);
    internal long SemanticFileCount => Interlocked.Read(ref _semanticFileCount);
    internal Action? BeforeEvictionForTests { private get; init; }

    // Test assemblies install this once during module initialization, before shared-cache access.
    internal static bool OverrideDefaultDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Shared.IsValueCreated) return false;
        _defaultDirectoryOverride = directory;
        return true;
    }

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
        if (directory is null || candidate.Length == 0 || !SourceReaderFileFacts.HasUniquePaths(candidate)) return null;
        try
        {
            var hashes = candidate.ToDictionary(file => file.Path, file => file.ContentHash, StringComparer.Ordinal);
            SourceReaderFileFacts[]? best = null;
            var bestOverlap = 0;
            // Bound JSON reads; newest wins ties. No store or caller changes are needed for baseline discovery.
            foreach (var path in NewestFirst(new DirectoryInfo(directory).EnumerateFiles("*.json"),
                         file => file.LastWriteTimeUtc, file => file.Name).Take(RetainedGenerations))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path.FullName));
                    if (entry is null || entry.FormatVersion != FormatVersion || entry.Key != Path.GetFileNameWithoutExtension(path.Name) ||
                        entry.ReaderOwners is null || entry.ReaderOwners.Any(string.IsNullOrWhiteSpace) ||
                        entry.Files is null || entry.FilesDigest != Digest(entry.Key, entry.Files) ||
                        !SourceReaderFileFacts.Valid(entry.Files)) continue;
                    var overlap = entry.Files.Count(file => hashes.GetValueOrDefault(file.Path) == file.ContentHash);
                    if (overlap <= bestOverlap || (long)overlap * 2 < candidate.Length) continue;
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
                entry.ReaderOwners is null || entry.ReaderOwners.Any(string.IsNullOrWhiteSpace) ||
                entry.AssemblyScans is null || entry.AssemblyScans.Any(file => file is null || !file.IsValid)) return false;
            analysis = new(entry.DeclaresSnapshotCache,
                entry.ReaderOwners.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
            { AssemblyScans = entry.AssemblyScans };
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
            var root = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(FormatVersion, key,
                analysis.ReaderOwners.ToArray(), analysis.DeclaresSnapshotCache, analysis.AssemblyScans.ToArray(), files,
                files is null ? null : Digest(key, files))));
            // Keep the eviction snapshot valid across processes and directory aliases. Staging and
            // serialization stay outside the lease because temporary files are never pruned.
            using var lease = AcquirePublicationLease(root);
            var published = new FileInfo(destination);
            var overwrite = false;
            if (File.Exists(destination))
            {
                // FileInfo created from the requested path would report its spelling even for a
                // case-insensitive alias. Check the actual directory entry before replacing it.
                overwrite = root.EnumerateFiles(published.Name)
                    .Any(file => file.Name == published.Name && IsOwnedGeneration(file));
                if (!overwrite) return;
            }

            // Publish a closed, complete entry by renaming within the destination filesystem.
            File.Move(temporary, destination, overwrite);
            PruneOlderGenerations(root, published);
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

    private static FileStream AcquirePublicationLease(DirectoryInfo root)
    {
        // The lease file stays in place so competing opens always refer to the same file.
        var path = Path.Combine(root.FullName, PublicationLeaseFileName);
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(25);
            }
        }
    }

    private static IOrderedEnumerable<T> NewestFirst<T>(
        IEnumerable<T> entries, Func<T, DateTime> lastWriteTime, Func<T, string> name) =>
        entries.OrderByDescending(lastWriteTime).ThenBy(name, StringComparer.Ordinal);

    private static bool IsOwnedGeneration(FileInfo file) =>
        file.Name.Length == 69 && file.Name.EndsWith(".json", StringComparison.Ordinal)
        && file.Name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0
        && (file.Attributes & FileAttributes.ReparsePoint) == 0;

    private void PruneOlderGenerations(DirectoryInfo root, FileInfo published)
    {
        // A missing entry is always a cache miss. Only evict completed entries in our key namespace.
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) return;
        // Reserve a slot for this publication even if older writes have future or tied timestamps.
        var otherSlots = IsOwnedGeneration(published) ? RetainedGenerations - 1 : RetainedGenerations;
        var otherGenerations = root.EnumerateFiles("*.json")
            .Where(file => file.Name != published.Name && IsOwnedGeneration(file)).ToArray();
        if (otherGenerations.Length <= otherSlots) return;

        var snapshots = OperatingSystem.IsWindows()
            ? otherGenerations.Select(WindowsEviction.Capture).OfType<GenerationSnapshot>()
            : otherGenerations.Select(file => new GenerationSnapshot(file, file.LastWriteTimeUtc, null));
        var expired = NewestFirst(snapshots, entry => entry.LastWriteTimeUtc, entry => entry.File.Name)
            .Skip(otherSlots).ToArray();
        if (expired.Length == 0) return;

        BeforeEvictionForTests?.Invoke();
        foreach (var entry in expired)
        {
            try
            {
                if (entry.FileId is { } fileId) WindowsEviction.DeleteIfCurrent(entry, fileId);
                else entry.File.Delete();
            }
            catch (IOException) { /* Concurrent writer or reader. Retry on the next publication. */ }
            catch (UnauthorizedAccessException) { /* A locked entry must not prevent other evictions. */ }
        }
    }

    private sealed record GenerationSnapshot(FileInfo File, DateTime LastWriteTimeUtc, GenerationFileId? FileId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenerationFileId(ulong VolumeSerialNumber, ulong Low, ulong High);

    private static class WindowsEviction
    {
        private const uint ReadAttributes = 0x80;
        private const uint DeleteAccess = 0x10000;
        private const uint OpenReparsePoint = 0x200000;
        private const int FileDispositionInfo = 4;
        private const int FileIdInfo = 18;
        private static readonly uint FileIdInfoSize = (uint)Marshal.SizeOf<GenerationFileId>();

        internal static GenerationSnapshot? Capture(FileInfo file)
        {
            using var handle = Open(file.FullName, ReadAttributes, FileShare.ReadWrite | FileShare.Delete);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out var id, FileIdInfoSize)
                || (File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0) return null;
            return new(file, File.GetLastWriteTimeUtc(handle), id);
        }

        internal static void DeleteIfCurrent(GenerationSnapshot entry, GenerationFileId expectedId)
        {
            // Older builds do not take the publication lease. Revalidate the identity under a
            // delete-capable handle and mark that object for deletion without reopening its path.
            using var handle = Open(entry.File.FullName, ReadAttributes | DeleteAccess, FileShare.Read);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out var actualId, FileIdInfoSize)
                || actualId != expectedId || (File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0
                || File.GetLastWriteTimeUtc(handle) != entry.LastWriteTimeUtc) return;
            byte deleteFile = 1;
            _ = SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, sizeof(byte));
        }

        private static SafeFileHandle Open(string path, uint access, FileShare share) =>
            CreateFileW(path, access, share, IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
            IntPtr securityAttributes, FileMode mode, uint flags, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
            out GenerationFileId information, uint size);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
            ref byte information, uint size);
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
        [property: JsonRequired] AssemblyScanningGuardTestClasses.ScanFile[]? AssemblyScans,
        SourceReaderFileFacts[]? Files,
        string? FilesDigest);

    private sealed record ResultEnvelope(
        [property: JsonRequired] int FormatVersion,
        [property: JsonRequired] string Key,
        [property: JsonRequired] string[]? ReaderOwners,
        [property: JsonRequired] bool DeclaresSnapshotCache,
        [property: JsonRequired] AssemblyScanningGuardTestClasses.ScanFile[]? AssemblyScans);

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
