using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class FocusedEvidenceParsedRootCache
{
    // The default focused project has more than 4096 sources; a smaller LRU thrashes on each scan.
    internal const int Capacity = 16384;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static Store Shared { get; } = new(Capacity);
    private static readonly object ObserverSync = new();
    private static ImmutableList<ParseObservation> _observers = [];
    private static long _parseCount;

    internal static long ParseCount => Interlocked.Read(ref _parseCount);

    internal static void RecordParse(string path)
    {
        Interlocked.Increment(ref _parseCount);
        var observers = Volatile.Read(ref _observers);
        if (observers.IsEmpty) return;
        var fullPath = Path.GetFullPath(path);
        foreach (var observer in observers) observer.Record(fullPath);
    }

    internal static ParseObservation Observe(string rootDirectory)
    {
        var observation = new ParseObservation(rootDirectory);
        lock (ObserverSync) _observers = _observers.Add(observation);
        return observation;
    }

    internal sealed class ParseObservation(string rootDirectory) : IDisposable
    {
        private readonly string _prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) +
            Path.DirectorySeparatorChar;
        private readonly ConcurrentDictionary<string, long> _counts = new(PathComparer);

        internal long CountFor(string path) => _counts.GetValueOrDefault(Path.GetFullPath(path));
        internal long Total => _counts.Values.Sum();

        internal void Record(string fullPath)
        {
            if (fullPath.StartsWith(_prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                _counts.AddOrUpdate(fullPath, 1, static (_, count) => count + 1);
        }

        public void Dispose()
        {
            lock (ObserverSync) _observers = _observers.Remove(this);
        }
    }

    internal readonly record struct DeclaredTypeName(string SimpleName, string QualifiedName);
    internal sealed record ParsedFile(CompilationUnitSyntax Root, ImmutableArray<DeclaredTypeName> Names);
    private readonly record struct Fingerprint(long Size, DateTime LastWriteTimeUtc);

    internal static bool FocusedEvidenceTypeMatches(DeclaredTypeName declaration, string className)
    {
        var requestedName = className.Trim();
        return declaration.SimpleName.Equals(requestedName, StringComparison.Ordinal) ||
            declaration.QualifiedName.Equals(requestedName, StringComparison.Ordinal) ||
            declaration.QualifiedName.EndsWith('.' + requestedName, StringComparison.Ordinal);
    }

    internal static DeclaredTypeName GetDeclaredTypeName(TypeDeclarationSyntax declaration)
    {
        var namespaceNames = declaration.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString());
        var containingTypeNames = declaration.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Identifier.ValueText);
        return new(declaration.Identifier.ValueText, string.Join(
            '.', namespaceNames.Concat(containingTypeNames).Append(declaration.Identifier.ValueText)));
    }

    private static ParsedFile Create(CompilationUnitSyntax root) => new(root,
        root.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(GetDeclaredTypeName).ToImmutableArray());

    private static bool TryFingerprint(string fullPath, out Fingerprint fingerprint)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (file.Exists)
            {
                fingerprint = new(file.Length, file.LastWriteTimeUtc);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Metadata failures must not replace the reader's exception or return stale success.
        }
        fingerprint = default;
        return false;
    }
}
