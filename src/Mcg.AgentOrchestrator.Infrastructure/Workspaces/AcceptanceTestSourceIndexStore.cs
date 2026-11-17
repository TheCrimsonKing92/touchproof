using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Process-lifetime metadata and declared names, published atomically per full search root.</summary>
internal static class AcceptanceTestSourceIndexStore
{
    private static readonly ConcurrentDictionary<string, RootEntry> Roots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static FrozenDictionary<string, ImmutableArray<string>>? GetIndex(
        string searchRoot, Func<string, long, DateTime, CompilationUnitSyntax?> readSource)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(searchRoot));
            var entry = Roots.GetOrAdd(root, static _ => new RootEntry());
            lock (entry.Sync)
            {
                var previous = entry.Snapshot;
                // Exact spelling matters for file paths, including a case-only rename on Windows.
                var files = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
                var changed = previous is null;
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.cs", SearchOption.AllDirectories))
                {
                    var path = file.FullName;
                    if (!AcceptanceTestSourceResolver.IsWithinDirectory(path, root) ||
                        AcceptanceTestSourceResolver.HasGeneratedPathSegment(path)) continue;

                    var size = file.Length;
                    var lastWrite = file.LastWriteTimeUtc;
                    if (previous is not null && previous.Files.TryGetValue(path, out var cached) &&
                        cached.Size == size && cached.LastWriteTimeUtc == lastWrite)
                    {
                        files.Add(path, cached);
                        continue;
                    }

                    changed = true;
                    // Bind the parse to these observations: an earlier cached parse may be stale.
                    // The Request owns parsing and read accounting. Unreadable files are retried
                    // by later requests rather than remembered as successfully processed files.
                    var syntax = readSource(path, size, lastWrite);
                    if (syntax is null) continue;
                    files.Add(path, new SourceFile(size, lastWrite,
                        AcceptanceTestSourceResolver.ExtractTypeNames(syntax).ToImmutableArray()));
                }

                if (!changed && files.Count == previous!.Files.Count) return previous.Index;

                var matches = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var (path, file) in files)
                foreach (var name in file.Names)
                {
                    if (!matches.TryGetValue(name, out var paths)) matches[name] = paths = [];
                    paths.Add(path);
                }

                // Keep full paths here: each Request owns repository-relative conversion and sorting.
                var index = matches.ToFrozenDictionary(pair => pair.Key,
                    pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
                entry.Snapshot = new Snapshot(files.ToFrozenDictionary(StringComparer.Ordinal), index);
                return index;
            }
        }
        catch (Exception ex) when (AcceptanceTestSourceResolver.IsResolutionFailure(ex))
        {
            // Never publish a partial refresh or return an older success for this failed lookup.
            return null;
        }
    }

    private sealed class RootEntry
    {
        internal object Sync { get; } = new();
        internal Snapshot? Snapshot { get; set; }
    }

    private sealed record SourceFile(long Size, DateTime LastWriteTimeUtc, ImmutableArray<string> Names);
    private sealed record Snapshot(FrozenDictionary<string, SourceFile> Files,
        FrozenDictionary<string, ImmutableArray<string>> Index);
}
