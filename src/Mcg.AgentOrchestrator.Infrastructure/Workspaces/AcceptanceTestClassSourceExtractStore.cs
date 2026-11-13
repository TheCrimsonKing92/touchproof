using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Rebuildable file-local facts, shared by processes and worktrees belonging to one owner.
internal sealed class AcceptanceTestClassSourceExtractStore(string directory)
{
    internal const string DirectoryName = "acceptance-test-class-source-extracts";
    // Bump on changes to serialized extract/ancestry records OR extraction semantics in
    // AcceptanceTestClassSourceScanner and AcceptanceTestClassSourceScannerFactAncestry.
    internal const int ExtractorVersion = 1;

    internal static AcceptanceTestClassSourceExtractStore? Resolve(string worktreePath)
    {
        try
        {
            var state = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath),
                ".orchestrator");
            // Disposable source trees without owner state stay free of persistent artifacts.
            return Directory.Exists(state) ? new(Path.Combine(state, DirectoryName)) : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    internal static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal string EntryPath(string hash) => Path.Combine(directory,
        ExtractorVersion.ToString(CultureInfo.InvariantCulture), hash + ".json");

    internal bool TryRead(string hash, out AcceptanceTestClassSourceScanner.FileExtract extract)
    {
        extract = null!;
        try
        {
            var entry = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(EntryPath(hash)));
            if (entry is null || entry.ExtractorVersion != ExtractorVersion || entry.ContentHash != hash ||
                !IsUsable(entry.Extract)) return false;
            extract = entry.Extract!;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void Write(string hash, AcceptanceTestClassSourceScanner.FileExtract extract)
    {
        string? temporary = null;
        try
        {
            var destination = EntryPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(ExtractorVersion, hash, extract)));
            // Closed, complete JSON is published by a rename on the same filesystem.
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

    private static bool IsUsable(AcceptanceTestClassSourceScanner.FileExtract? extract)
    {
        if (extract?.Constants is null || extract.Classes is null ||
            extract.Constants.Any(item => item is null || item.Key is null || item.Value is null) ||
            extract.Classes.Any(item => item is null || item.FullName is null || item.ShortName is null ||
                item.Collection is null || !Enum.IsDefined(item.Collection.Kind) ||
                (item.Collection.Value is null && item.Collection.Kind is not
                    (AcceptanceTestClassSourceScanner.CollectionExpressionKind.None or
                     AcceptanceTestClassSourceScanner.CollectionExpressionKind.Unresolvable)))) return false;
        var ancestry = extract.Ancestry;
        return ancestry is not null && ancestry.Classes is not null && ancestry.GlobalUsings is not null &&
            ancestry.GlobalUsings.All(IsUsableUsing) && ancestry.Classes.All(item =>
                item is not null && item.FullName is not null && item.LookupName is not null &&
                item.Attributes is not null && item.Attributes.All(attribute => attribute is not null) &&
                item.Scopes is not null && item.Scopes.All(scope => scope is not null &&
                    scope.Prefix is not null && scope.Usings is not null && scope.Usings.All(IsUsableUsing)));
    }

    private static bool IsUsableUsing(AcceptanceTestClassSourceScannerFactAncestry.AncestryUsing? item) =>
        item is not null && item.Target is not null;

    private sealed record Envelope(int ExtractorVersion, string ContentHash,
        AcceptanceTestClassSourceScanner.FileExtract? Extract);
}
