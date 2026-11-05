using static Mcg.AgentOrchestrator.Infrastructure.GitRevisionReader;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Positive evidence from ref files only. Uncertainty belongs to git, never to a cached id.
internal static class GitRefFileResolver
{
    internal static bool TryResolve(string directory, string revision, out string objectId)
        => TryResolve(directory, revision, out objectId, Environment.GetEnvironmentVariable);

    internal static bool TryResolve(string directory, string revision, out string objectId,
        Func<string, string?> environment)
    {
        objectId = string.Empty;
        try
        {
            if (revision != "HEAD" && !IsFullBranchRef(revision)) return false;
            if (!TryLocateRefStore(directory, environment, out var gitDirectory, out var commonDirectory) ||
                !HasConclusiveWorkTreeConfig(Path.Combine(commonDirectory, "config"), out var idLength)) return false;
            var headPath = Path.Combine(gitDirectory, "HEAD");
            var packedPath = Path.Combine(commonDirectory, "packed-refs");
            if (Locked(headPath) || Locked(packedPath)) return false;
            var head = ReadBranchMetadataLine(headPath);
            string? id;
            if (revision == "HEAD" && ValidId(head, idLength)) id = head;
            else
            {
                var reference = revision;
                if (revision == "HEAD")
                {
                    const string prefix = "ref: ";
                    if (head is null || !head.StartsWith(prefix, StringComparison.Ordinal)) return false;
                    reference = head[prefix.Length..];
                }
                if (!IsFullBranchRef(reference) ||
                    !TryProbeLooseRef(commonDirectory, reference, out var exists, out var refPath) ||
                    Locked(refPath)) return false;
                if (exists)
                {
                    id = ReadBranchMetadataLine(refPath);
                    if (!ValidId(id, idLength) || ReadBranchMetadataLine(refPath) != id) return false;
                }
                else
                {
                    if (!TryReadPackedRefs(commonDirectory, out var entries)) return false;
                    var matches = entries.Where(entry => entry.Name.Equals(reference,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
                    if (matches.Length != 1 || matches[0].Name != reference) return false;
                    id = matches[0].Id;
                    // A loose writer takes precedence over the packed value, including malformed content.
                    if (!TryProbeLooseRef(commonDirectory, reference, out exists, out _) || exists ||
                        !TryReadPackedRefs(commonDirectory, out var reread) || !entries.SequenceEqual(reread)) return false;
                }
                if (Locked(refPath)) return false;
            }
            if (!ValidId(id, idLength) || Locked(headPath) || Locked(packedPath) ||
                ReadBranchMetadataLine(headPath) != head ||
                !TryLocateRefStore(directory, environment, out var finalGit, out var finalCommon) ||
                finalGit != gitDirectory || finalCommon != commonDirectory) return false;
            objectId = id!;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsFullBranchRef(string revision)
    {
        const string prefix = "refs/heads/";
        if (!revision.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var branch = revision[prefix.Length..];
        return IsBranchRefName(branch) && !HasDescribeObjectSuffix(branch) &&
            (!OperatingSystem.IsWindows() || !branch.Split('/').Any(IsAmbiguousWindowsRefComponent));
    }

    private static bool Locked(string path) => WorkTreeMetadataEntryExists(path + ".lock");

    // The existing syntax guard accepts either hash length and uppercase. Positive answers also
    // require the repository's declared format and git's canonical lowercase ref representation.
    private static bool ValidId(string? id, int length)
        => id is not null && IsBranchObjectId(id) && id.Length == length && !id.Any(char.IsAsciiLetterUpper);
}
