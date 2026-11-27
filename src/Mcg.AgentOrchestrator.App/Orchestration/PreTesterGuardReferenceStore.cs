using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using GuardReference = Mcg.AgentOrchestrator.App.Orchestration.PreTesterGuardReferenceReader.GuardReference;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PreTesterGuardReferenceStore(string directory)
{
    internal const string DirectoryName = "pre-tester-guard-references";
    // Bump when the reference reader's extraction semantics or the serialized shape changes.
    internal const int ReaderVersion = 1;

    internal static PreTesterGuardReferenceStore? ResolveForFile(string file)
    {
        try
        {
            for (var ancestor = new FileInfo(file).Directory; ancestor is not null; ancestor = ancestor.Parent)
            {
                var gitMarker = Path.Combine(ancestor.FullName, ".git");
                if (File.Exists(gitMarker) || Directory.Exists(gitMarker))
                    return Resolve(ancestor.FullName);
            }
            return null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static PreTesterGuardReferenceStore? Resolve(string worktreePath)
    {
        var state = Path.Combine(AcceptancePartitionVerdictCache.ResolveHostStateRoot(worktreePath), ".orchestrator");
        return Directory.Exists(state) ? new(Path.Combine(state, DirectoryName)) : null;
    }

    internal string EntryPath(string hash) => Path.Combine(directory,
        ReaderVersion.ToString(CultureInfo.InvariantCulture), hash + ".json");

    internal bool TryRead(string hash, out GuardReference[] references)
    {
        references = [];
        try
        {
            var entry = JsonSerializer.Deserialize<Envelope>(SelectionFileReadProbe.ReadAllText(EntryPath(hash)));
            if (entry is null || entry.ReaderVersion != ReaderVersion || entry.ContentHash != hash ||
                entry.References is null || !entry.References.All(IsUsable)) return false;

            references = entry.References;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    internal void Write(string hash, GuardReference[] references)
    {
        string? temporary = null;
        try
        {
            var destination = EntryPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Envelope(ReaderVersion, hash, references)));
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

    private static bool IsUsable(GuardReference? reference) =>
        reference is { Class: not null, Kind: not null, Pattern: not null, Path: not null } &&
        reference.Path.All(segment => segment is not null);

    private sealed record Envelope(int ReaderVersion, string ContentHash, GuardReference[]? References);
}
