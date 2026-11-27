using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Rebuildable whole-source analysis shared by selections, processes and worktrees for one user.
internal sealed class SourceReaderAnalysisCache(string? directory)
{
    // Bump for serialized shape OR analysis semantics changes in SourceReadingGuardTestClasses
    // or WholeTreeSourceReaderDetector.
    internal const int FormatVersion = 11;
    internal const int RetainedGenerations = 8;
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
    internal Action? BeforePublicationForTests { private get; init; }
    internal Action? AfterWindowsDestinationClearedForTests { private get; init; }
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
        var factsStore = new SourceReaderFileFactsStore(directory);
        var identities = directory is null ? [] : sources.Trees.Select(tree =>
            (Path: SourceReaderFileFacts.RelativePath(sources, tree),
                ContentHash: SourceReaderFileFacts.Hash(tree.GetText().ToString()))).ToArray();
        var baseline = factsStore.FindBaseline(identities, out var loadedDigests);
        var previous = baseline?.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var candidate = baseline is null ? [] : sources.Trees.Select((tree, index) =>
        {
            var (path, hash) = identities[index];
            return previous!.TryGetValue(path, out var file) && file.ContentHash == hash
                ? file : SourceReaderFileFacts.Describe(sources, tree);
        }).ToArray();
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
            // Plan includes every changed file and every reference to old or new changed names.
            // Only hash-equal, non-invalidated files reach the baseline branch below.
            files = candidate.Select(file => replacements.TryGetValue(file.Path, out var replacement)
                ? replacement : previous![file.Path]).ToArray();
            if (!SourceReaderFileFacts.Valid(files) || LostResolution(baseline!, fresh))
                files = Full(); // Dangling identities or lost bindings cannot be partially trusted.
        }
        var analysis = evaluate(sources, files);
        Write(key, analysis);
        factsStore.Publish(key, files, loadedDigests);
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

    internal bool TryRead(string key, out Analysis analysis)
    {
        analysis = null!;
        try
        {
            if (EntryPath(key) is not { } path) return false;
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

    internal void Write(string key, Analysis analysis)
    {
        string? temporary = null;
        try
        {
            if (EntryPath(key) is not { } destination) return;
            var root = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ResultEnvelope(FormatVersion, key,
                analysis.ReaderOwners.ToArray(), analysis.DeclaresSnapshotCache, analysis.AssemblyScans.ToArray())));
            // Keep the eviction snapshot valid across processes and directory aliases. Staging and
            // serialization stay outside the lease because temporary files are never pruned.
            using var lease = AcquirePublicationLease(root);
            var published = new FileInfo(destination);
            BeforePublicationForTests?.Invoke();
            if (!Publish(root, published, temporary)) return;
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

    private bool Publish(DirectoryInfo root, FileInfo destination, string temporary)
    {
        // The staging file is closed and complete before either move. On Windows, remove an
        // existing owned entry by handle, then publish without clobbering a newly arrived name.
        if (OperatingSystem.IsWindows())
        {
            if (!WindowsGeneration.TryClearDestination(destination)) return false;
            AfterWindowsDestinationClearedForTests?.Invoke();
            File.Move(temporary, destination.FullName);
            return true;
        }

        var overwrite = File.Exists(destination.FullName);
        if (overwrite && !root.EnumerateFiles(destination.Name)
            .Any(file => file.Name == destination.Name && IsOwnedGeneration(file))) return false;
        File.Move(temporary, destination.FullName, overwrite);
        return true;
    }

    internal static FileStream AcquirePublicationLease(DirectoryInfo root)
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

    internal static IOrderedEnumerable<T> NewestFirst<T>(
        IEnumerable<T> entries, Func<T, DateTime> lastWriteTime, Func<T, string> name) =>
        entries.OrderByDescending(lastWriteTime).ThenBy(name, StringComparer.Ordinal);

    internal static bool IsOwnedGeneration(FileInfo file) =>
        IsGenerationName(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0;

    private static bool IsGenerationName(string name) =>
        name.Length == 69 && name.EndsWith(".json", StringComparison.Ordinal)
        && name.AsSpan(0, 64).IndexOfAnyExcept("0123456789abcdef") < 0;

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
            ? otherGenerations.Select(WindowsGeneration.Capture).OfType<GenerationSnapshot>()
            : otherGenerations.Select(file => new GenerationSnapshot(file, file.LastWriteTimeUtc, null));
        var expired = NewestFirst(snapshots, entry => entry.LastWriteTimeUtc, entry => entry.File.Name)
            .Skip(otherSlots).ToArray();
        if (expired.Length == 0) return;

        BeforeEvictionForTests?.Invoke();
        foreach (var entry in expired)
        {
            try
            {
                if (entry.FileId is { } fileId) WindowsGeneration.DeleteIfCurrent(entry, fileId);
                else entry.File.Delete();
            }
            catch (IOException) { /* Concurrent writer or reader. Retry on the next publication. */ }
            catch (UnauthorizedAccessException) { /* A locked entry must not prevent other evictions. */ }
        }
    }

    private sealed record GenerationSnapshot(FileInfo File, DateTime LastWriteTimeUtc, GenerationFileId? FileId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GenerationFileId(ulong VolumeSerialNumber, ulong Low, ulong High);

    private static class WindowsGeneration
    {
        private const uint ReadAttributes = 0x80;
        private const uint DeleteAccess = 0x10000;
        private const uint OpenReparsePoint = 0x200000;
        private const int FileDispositionInfo = 4;
        private const int FileIdInfo = 18;
        private const int FileNotFound = 2;
        private static readonly uint FileIdInfoSize = (uint)Marshal.SizeOf<GenerationFileId>();

        internal static bool TryClearDestination(FileInfo destination)
        {
            using var handle = OpenForDeletion(destination.FullName);
            if (handle.IsInvalid) return Marshal.GetLastPInvokeError() == FileNotFound;
            return IsCurrentOwnedEntry(handle, destination.Name) && MarkForDeletion(handle);
        }

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
            using var handle = OpenForDeletion(entry.File.FullName);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out var actualId, FileIdInfoSize)
                || actualId != expectedId || !IsCurrentOwnedEntry(handle, entry.File.Name)
                || File.GetLastWriteTimeUtc(handle) != entry.LastWriteTimeUtc) return;
            MarkForDeletion(handle);
        }

        private static bool IsCurrentOwnedEntry(SafeFileHandle handle, string expectedName)
        {
            if (!IsGenerationName(expectedName) || (File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
                return false;

            // Windows can open a case alias under the requested spelling. Query the filesystem's
            // spelling while the handle denies rename and delete, then act on that same handle.
            var path = new StringBuilder(260);
            var length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
            if (length >= path.Capacity)
            {
                path.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
            }
            return length > 0 && length < path.Capacity
                && string.Equals(Path.GetFileName(path.ToString()), expectedName, StringComparison.Ordinal);
        }

        private static bool MarkForDeletion(SafeFileHandle handle)
        {
            byte deleteFile = 1;
            return SetFileInformationByHandle(handle, FileDispositionInfo, ref deleteFile, sizeof(byte));
        }

        private static SafeFileHandle OpenForDeletion(string path) =>
            Open(path, ReadAttributes | DeleteAccess, FileShare.Read);

        private static SafeFileHandle Open(string path, uint access, FileShare share) =>
            CreateFileW(path, access, share, IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
            IntPtr securityAttributes, FileMode mode, uint flags, IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

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
