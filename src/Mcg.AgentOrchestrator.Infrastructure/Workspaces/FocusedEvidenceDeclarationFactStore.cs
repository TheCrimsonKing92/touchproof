using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.CodeAnalysis.CSharp;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class FocusedEvidenceDeclarationFactStore(FocusedEvidenceParsedRootCache.Store? rootStore = null)
{
    internal const string DirectoryName = "focused-evidence-declaration-facts";
    // Bump if the serialized envelope or facts change incompatibly, or if extraction changes
    // simple/qualified names (including nesting), class/record classification, or Fact/Theory recognition or ownership.
    internal const int ExtractorVersion = 1;

    private static readonly FocusedEvidenceDeclarationFactStore Shared = new();
    private static readonly AsyncLocal<FocusedEvidenceDeclarationFactStore?> Override = new();
    private readonly FocusedEvidenceParsedRootCache.Store _rootStore = rootStore ?? FocusedEvidenceParsedRootCache.Shared;
    private readonly object _sync = new();
    private readonly Dictionary<string, MemoryEntry> _paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();

    internal static FocusedEvidenceDeclarationFactStore Current => Override.Value ?? Shared;

    internal static IDisposable Use(FocusedEvidenceDeclarationFactStore store)
    {
        var previous = Override.Value;
        Override.Value = store;
        return new RestoreStore(previous);
    }

    internal FocusedEvidenceDeclarationFacts Read(string path, string? worktreePath = null)
    {
        var fullPath = Path.GetFullPath(path);
        var source = SelectionFileReadProbe.ReadAllText(path);
        var hash = AcceptanceTestClassSourceExtractStore.HashText(source);
        lock (_sync)
        {
            if (_paths.TryGetValue(fullPath, out var cached) && cached.ContentHash == hash)
                return cached.Facts;
        }

        var directory = ResolveDirectory(worktreePath ?? FindWorktree(fullPath));
        var entryPath = directory is null ? null : EntryPath(directory, hash);
        if (entryPath is not null && TryRead(entryPath, hash) is { } stored)
            return Remember(fullPath, hash, stored);

        var root = CSharpSourceFileRootReader.ReadFile(_rootStore, path).Root;
        // The root cache can reuse a metadata match after a rewrite. Use the hashed snapshot if the text differs.
        if (root.SyntaxTree.GetText().ToString() != source)
        {
            root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
            FocusedEvidenceParsedRootCache.RecordParse(path);
        }
        var facts = FocusedEvidenceDeclarationFacts.Extract(root);
        if (entryPath is not null) Write(entryPath, hash, facts);
        return Remember(fullPath, hash, facts);
    }

    internal static string EntryPath(string directory, string hash) => Path.Combine(directory,
        ExtractorVersion.ToString(CultureInfo.InvariantCulture), hash + ".json");

    private FocusedEvidenceDeclarationFacts Remember(string fullPath, string hash,
        FocusedEvidenceDeclarationFacts facts)
    {
        lock (_sync)
        {
            if (!_paths.ContainsKey(fullPath))
            {
                if (_paths.Count == FocusedEvidenceParsedRootCache.Capacity)
                    _paths.Remove(_insertionOrder.Dequeue());
                _insertionOrder.Enqueue(fullPath);
            }
            _paths[fullPath] = new(hash, facts);
        }
        return facts;
    }

    private static string? FindWorktree(string fullPath)
    {
        for (var ancestor = Path.GetDirectoryName(fullPath); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            var gitMarker = Path.Combine(ancestor, ".git");
            if (File.Exists(gitMarker) || Directory.Exists(gitMarker)) return ancestor;
        }
        return null;
    }

    private static string? ResolveDirectory(string? worktreePath)
    {
        if (worktreePath is null) return null;
        try
        {
            var state = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator");
            return Directory.Exists(state) ? Path.Combine(state, DirectoryName) : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static FocusedEvidenceDeclarationFacts? TryRead(string path, string hash)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Envelope>(SelectionFileReadProbe.ReadAllText(path));
            if (entry is null || entry.ExtractorVersion != ExtractorVersion || entry.ContentHash != hash ||
                entry.Facts is not { } facts || facts.Types.IsDefault ||
                facts.Types.Any(type => type is null || type.SimpleName is null || type.QualifiedName is null ||
                    type.TestMethodNames.IsDefault || type.TestMethodNames.Any(name => name is null)))
                return null;
            return facts;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static void Write(string destination, string hash, FocusedEvidenceDeclarationFacts facts)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(ExtractorVersion, hash, facts)));
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

    private sealed record Envelope(int ExtractorVersion, string ContentHash, FocusedEvidenceDeclarationFacts? Facts);
    private sealed record MemoryEntry(string ContentHash, FocusedEvidenceDeclarationFacts Facts);

    private sealed class RestoreStore(FocusedEvidenceDeclarationFactStore? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
