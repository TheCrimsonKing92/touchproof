using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class FocusedEvidenceParsedRootCache
{
    internal sealed class Store
    {
        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly Dictionary<string, LinkedListNode<PathEntry>> _paths = new(PathComparer);
        private readonly LinkedList<PathEntry> _pathRecency = new();
        private readonly Dictionary<string, LinkedListNode<ContentEntry>> _contents = new(StringComparer.Ordinal);
        private readonly LinkedList<ContentEntry> _contentRecency = new();
        private long _contentVersion;
        private readonly Dictionary<string, Dictionary<Fingerprint, Lazy<ParsedFile>>> _loads = new(PathComparer);
        private readonly Dictionary<string, PendingContent> _contentLoads = new(StringComparer.Ordinal);
        // A caller may hold only ParsedFile.Root. Keep its wrapper available for as long as that root is live.
        private readonly ConditionalWeakTable<CompilationUnitSyntax, ParsedFile> _wrappers = new();

        internal Store(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        internal int Count
        {
            get { lock (_sync) return _contents.Count; }
        }

        internal ParsedFile Get(string path, Func<string, CompilationUnitSyntax> loadRoot) =>
            Get(path, (sourcePath, _) => loadRoot(sourcePath));

        internal ParsedFile Get(string path, Func<string, byte[], CompilationUnitSyntax> parse, Action? onParse = null)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return LoadUncached(path, parse, onParse);
            }

            if (!TryFingerprint(fullPath, out var fingerprint))
            {
                lock (_sync) RemovePath(fullPath);
                // Let the reader determine the exception when metadata is unavailable.
                return LoadUncached(path, parse, onParse);
            }

            Lazy<ParsedFile> pending;
            lock (_sync)
            {
                if (_paths.TryGetValue(fullPath, out var resident))
                {
                    var metadata = resident.Value;
                    if (metadata.Fingerprint == fingerprint && FindContent(metadata.Hash) is { } content)
                    {
                        _pathRecency.Remove(resident);
                        _pathRecency.AddFirst(resident);
                        return GetWrapper(metadata, content, path);
                    }
                    RemovePath(fullPath);
                }

                if (!_loads.TryGetValue(fullPath, out var versions))
                    _loads.Add(fullPath, versions = new());
                if (!versions.TryGetValue(fingerprint, out pending!))
                {
                    pending = new(() => Load(path, fullPath, fingerprint, parse, onParse),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    versions.Add(fingerprint, pending);
                }
            }

            return Relabel(pending.Value, path);
        }

        private ParsedFile Load(string path, string fullPath, Fingerprint fingerprint,
            Func<string, byte[], CompilationUnitSyntax> parse, Action? onParse)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                return LoadContent(path, fullPath, fingerprint, hash, bytes, parse, onParse);
            }
            finally
            {
                // Lazy preserves the original exception; drop the in-flight load so later calls can retry.
                lock (_sync)
                {
                    var versions = _loads[fullPath];
                    versions.Remove(fingerprint);
                    if (versions.Count == 0) _loads.Remove(fullPath);
                }
            }
        }

        private ParsedFile LoadContent(string path, string fullPath, Fingerprint fingerprint, string hash,
            byte[] bytes, Func<string, byte[], CompilationUnitSyntax> parse, Action? onParse)
        {
            PendingContent? pending = null;
            var ownsParse = false;
            ParsedFile? content;
            lock (_sync)
            {
                content = FindContent(hash)?.File;
                if (content is null)
                {
                    if (!_contentLoads.TryGetValue(hash, out pending))
                    {
                        pending = new(() => Parse(path, bytes, parse));
                        _contentLoads.Add(hash, pending);
                        ownsParse = true;
                    }
                    pending.Users++;
                }
            }

            try
            {
                content ??= pending!.File.Value;
                // Other paths can use the completed parse while this path's notification runs.
                if (ownsParse) onParse?.Invoke();
                if (!TryFingerprint(fullPath, out var after) || after != fingerprint)
                    return Relabel(content, path);

                lock (_sync)
                {
                    var entry = RememberContent(hash, content);
                    RemovePath(fullPath);
                    var metadata = new PathEntry(fullPath, fingerprint, hash);
                    _paths.Add(fullPath, _pathRecency.AddFirst(metadata));
                    if (_paths.Count > _capacity) RemovePath(_pathRecency.Last!.Value.Path);
                    return GetWrapper(metadata, entry, path);
                }
            }
            finally
            {
                if (pending is not null)
                {
                    lock (_sync)
                    {
                        if (--pending.Users == 0) _contentLoads.Remove(hash);
                    }
                }
            }
        }

        private ContentEntry? FindContent(string hash)
        {
            if (!_contents.TryGetValue(hash, out var resident)) return null;
            _contentRecency.Remove(resident);
            _contentRecency.AddFirst(resident);
            return resident.Value;
        }

        private ContentEntry RememberContent(string hash, ParsedFile content)
        {
            if (FindContent(hash) is { } resident) return resident;
            var entry = new ContentEntry(hash, content, ++_contentVersion);
            _contents.Add(hash, _contentRecency.AddFirst(entry));
            if (_contents.Count > _capacity)
            {
                _contents.Remove(_contentRecency.Last!.Value.Hash);
                _contentRecency.RemoveLast();
            }
            return entry;
        }

        private void RemovePath(string fullPath)
        {
            if (_paths.Remove(fullPath, out var resident)) _pathRecency.Remove(resident);
        }

        private ParsedFile GetWrapper(PathEntry metadata, ContentEntry content, string path)
        {
            if (metadata.ContentVersion == content.Version &&
                metadata.Root is not null && metadata.Root.TryGetTarget(out var root) &&
                root.SyntaxTree.FilePath == path && _wrappers.TryGetValue(root, out var wrapper))
                return wrapper;

            var file = Relabel(content.File, path);
            metadata.ContentVersion = content.Version;
            metadata.Root = new(file.Root);
            _wrappers.Add(file.Root, file);
            return file;
        }

        private static ParsedFile LoadUncached(string path, Func<string, byte[], CompilationUnitSyntax> parse,
            Action? onParse)
        {
            var content = Parse(path, File.ReadAllBytes(path), parse);
            onParse?.Invoke();
            return Relabel(content, path);
        }

        private static ParsedFile Parse(string path, byte[] bytes, Func<string, byte[], CompilationUnitSyntax> parse)
        {
            var root = parse(path, bytes);
            if (root.SyntaxTree.FilePath.Length != 0)
                root = root.SyntaxTree.WithFilePath(string.Empty).GetCompilationUnitRoot();
            return Create(root);
        }

        private static ParsedFile Relabel(ParsedFile file, string path) =>
            file.Root.SyntaxTree.FilePath == path
                ? file
                : new(file.Root.SyntaxTree.WithFilePath(path).GetCompilationUnitRoot(), file.Names);

        private sealed record PathEntry(string Path, Fingerprint Fingerprint, string Hash)
        {
            internal long ContentVersion { get; set; }
            internal WeakReference<CompilationUnitSyntax>? Root { get; set; }
        }

        private sealed record ContentEntry(string Hash, ParsedFile File, long Version);

        private sealed class PendingContent(Func<ParsedFile> parse)
        {
            internal Lazy<ParsedFile> File { get; } = new(parse, LazyThreadSafetyMode.ExecutionAndPublication);
            internal int Users { get; set; }
        }
    }
}
