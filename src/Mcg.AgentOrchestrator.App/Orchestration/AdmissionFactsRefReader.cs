using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.GitRevisionReader;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Observes literal ref IDs, not whether rev-parse would succeed. Git-backed derivation owns
// object/config validation. The general resolver's proof of git success deliberately rejects
// ordinary user/remote/commit settings; that is unsuitable for a lookup with no git fallback.
internal static class AdmissionFactsRefReader
{
    internal static bool TryResolve(string directory, string revision, out string objectId,
        Func<string, string?>? environment = null)
    {
        objectId = string.Empty;
        environment ??= Environment.GetEnvironmentVariable;
        try
        {
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_NAMESPACE",
                "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_COUNT" })
                if (environment(name) is not null) return false;
            var entry = Path.Combine(Path.GetFullPath(directory), ".git");
            if (!Ordinary(entry)) return false;
            var git = Directory.Exists(entry) ? entry : ResolvePointer(entry, "gitdir: ", directory);
            if (git is null || !OrdinaryDirectory(git)) return false;
            var commonPath = Path.Combine(git, "commondir");
            var common = WorkTreeMetadataEntryExists(commonPath) ? ResolvePointer(commonPath, "", git) : git;
            if (common is null || !OrdinaryDirectory(common) ||
                WorkTreeMetadataEntryExists(Path.Combine(common, "reftable")) ||
                WorkTreeMetadataEntryExists(Path.Combine(git, "reftable"))) return false;
            var headPath = Path.Combine(git, "HEAD");
            var packedPath = Path.Combine(common, "packed-refs");
            if (Locked(headPath) || Locked(packedPath) || !Ordinary(headPath)) return false;
            var head = ReadBranchMetadataLine(headPath);
            string? id;
            if (revision == "HEAD" && ValidId(head)) id = head;
            else
            {
                var reference = revision == "HEAD" && head?.StartsWith("ref: ", StringComparison.Ordinal) == true
                    ? head[5..] : revision;
                if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal) || !IsBranchRefName(reference) ||
                    reference.Split('/').Any(IsAmbiguousWindowsRefComponent) ||
                    !TryProbeLooseRef(common, reference, out var exists, out var path) || Locked(path)) return false;
                if (exists)
                {
                    id = ReadBranchMetadataLine(path);
                    if (!ValidId(id) || ReadBranchMetadataLine(path) != id) return false;
                }
                else
                {
                    if (!TryReadPackedRefs(common, out var entries)) return false;
                    var matches = entries.Where(item => item.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length != 1 || matches[0].Name != reference) return false;
                    id = matches[0].Id;
                    if (!TryProbeLooseRef(common, reference, out exists, out _) || exists ||
                        !TryReadPackedRefs(common, out var reread) || !entries.SequenceEqual(reread)) return false;
                }
                if (Locked(path)) return false;
            }
            if (!ValidId(id) || Locked(headPath) || Locked(packedPath) || ReadBranchMetadataLine(headPath) != head ||
                (!Directory.Exists(entry) && ResolvePointer(entry, "gitdir: ", directory) != git) ||
                (WorkTreeMetadataEntryExists(commonPath) ? ResolvePointer(commonPath, "", git) : git) != common) return false;
            objectId = id!;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? ResolvePointer(string file, string prefix, string directory)
    {
        if (!Ordinary(file)) return null;
        var text = ReadBranchMetadataLine(file);
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length
            ? Path.GetFullPath(text[prefix.Length..], directory) : null;
    }

    private static bool Ordinary(string path) => WorkTreeMetadataEntryExists(path) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
    private static bool OrdinaryDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static bool Locked(string path) => WorkTreeMetadataEntryExists(path + ".lock");
    private static bool ValidId(string? value) => value is not null && IsBranchObjectId(value) &&
        !value.Any(char.IsAsciiLetterUpper);
}
