using static Mcg.AgentOrchestrator.Infrastructure.GitRevisionReader;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

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
            foreach (var name in new[] { "GIT_NAMESPACE", "GIT_REPLACE_REF_BASE", "GIT_NO_REPLACE_OBJECTS", "GIT_CONFIG" })
                if (environment(name) is not null) return false;
            if (revision.EndsWith("^{commit}", StringComparison.Ordinal) ||
                revision.EndsWith("^{tree}", StringComparison.Ordinal))
                return TryResolveCommitExpression(directory, revision, environment, out objectId);
            if (!TryLocateRefStore(directory, environment, out var gitDirectory, out var commonDirectory) ||
                !HasConclusiveWorkTreeConfig(Path.Combine(commonDirectory, "config"), out var idLength)) return false;
            var shorthand = revision != "HEAD" && !IsFullBranchRef(revision) ? revision : null;
            if (shorthand is not null)
            {
                if (!CanUseBranchShorthand(gitDirectory, commonDirectory, shorthand)) return false;
                revision = "refs/heads/" + shorthand;
            }
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
                finalGit != gitDirectory || finalCommon != commonDirectory ||
                shorthand is not null && !CanUseBranchShorthand(finalGit, finalCommon, shorthand)) return false;
            objectId = id!;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Peel only a proved loose commit. Packed objects, tags, replacements and ambiguous
    // shorthand stay with Git; no object or ref value is cached across invocations.
    private static bool TryResolveCommitExpression(string directory, string revision,
        Func<string, string?> environment, out string objectId)
    {
        objectId = string.Empty;
        var tree = revision.EndsWith("^{tree}", StringComparison.Ordinal);
        var basis = revision[..^(tree ? 7 : 9)];
        if (basis.IndexOfAny(['^', '~', ':', '@']) >= 0) return false;
        if (!TryLocateRefStore(directory, environment, out var gitDirectory, out var commonDirectory) ||
            !HasConclusiveWorkTreeConfig(Path.Combine(commonDirectory, "config"), out var idLength)) return false;
        if (WorkTreeMetadataEntryExists(Path.Combine(commonDirectory, "refs", "replace")) ||
            WorkTreeMetadataEntryExists(Path.Combine(commonDirectory, "info", "grafts")) ||
            !TryReadPackedRefs(commonDirectory, out var packed) ||
            packed.Any(entry => entry.Name.StartsWith("refs/replace/", StringComparison.Ordinal))) return false;

        string commit;
        if (ValidId(basis, idLength))
        {
            if (!AlternativesAbsent(gitDirectory, commonDirectory, basis, includeBranch: true)) return false;
            commit = basis;
        }
        else
        {
            if (!TryResolve(directory, basis, out commit, environment)) return false;
        }
        if (!TryReadLooseObject(commonDirectory, commit, "commit", out var contents)) return false;
        // A valid commit begins with its tree. Do not interpret any other revision syntax.
        var lineEnd = Array.IndexOf(contents, (byte)'\n');
        if (lineEnd != 5 + idLength || !contents.AsSpan(0, 5).SequenceEqual("tree "u8)) return false;
        var treeId = Encoding.ASCII.GetString(contents, 5, idLength);
        if (!ValidId(treeId, idLength) || !HasOrdinaryCommitHeaders(contents.AsSpan(lineEnd + 1), idLength)) return false;
        if (!tree) { objectId = commit; return true; }
        if (!TryReadLooseObject(commonDirectory, treeId, "tree", out _)) return false;
        objectId = treeId;
        return true;
    }

    private static bool HasOrdinaryCommitHeaders(ReadOnlySpan<byte> headers, int idLength)
    {
        while (headers.StartsWith("parent "u8))
        {
            var end = headers.IndexOf((byte)'\n');
            if (end != 7 + idLength || !ValidId(Encoding.ASCII.GetString(headers[7..end]), idLength)) return false;
            headers = headers[(end + 1)..];
        }
        var authorEnd = headers.IndexOf((byte)'\n');
        if (authorEnd < 0 || !headers.StartsWith("author "u8)) return false;
        headers = headers[(authorEnd + 1)..];
        return headers.StartsWith("committer "u8) && headers.IndexOf("\n\n"u8) >= 0;
    }

    private static bool CanUseBranchShorthand(string gitDirectory, string commonDirectory, string name)
        => IsFullBranchRef("refs/heads/" + name) && !name.All(Uri.IsHexDigit) &&
            AlternativesAbsent(gitDirectory, commonDirectory, name, includeBranch: false);

    private static bool AlternativesAbsent(string gitDirectory, string commonDirectory, string name, bool includeBranch)
    {
        if (!TryReadPackedRefs(commonDirectory, out var packed)) return false;
        // Git's DWIM precedence includes pseudo refs, tags and remote refs. A branch
        // shorthand is safe only when every competing name is absent in both stores.
        foreach (var alternative in new[] { name, "refs/" + name, "refs/tags/" + name,
            "refs/remotes/" + name, "refs/remotes/" + name + "/HEAD" }
            .Concat(includeBranch ? ["refs/heads/" + name] : Array.Empty<string>()))
        {
            if (!TryProbeLooseRef(commonDirectory, alternative, out var exists, out _) || exists ||
                packed.Any(entry => entry.Name.Equals(alternative, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return false;
            if (gitDirectory != commonDirectory && WorkTreeMetadataEntryExists(Path.Combine(gitDirectory, alternative)))
                return false;
        }
        return true;
    }

    private static bool TryReadLooseObject(string commonDirectory, string id, string type, out byte[] contents)
    {
        contents = [];
        var path = Path.Combine(commonDirectory, "objects", id[..2], id[2..]);
        if (!HasOrdinaryDirectoryPath(Path.GetDirectoryName(path)!) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var file = File.OpenRead(path);
        // Bound both allocations and decompression work; larger objects use the reader.
        const int maximumBytes = 1_048_576;
        if (file.Length > maximumBytes) return false;
        using var compressed = new ZLibStream(file, CompressionMode.Decompress);
        using var expanded = new MemoryStream();
        Span<byte> buffer = stackalloc byte[4096];
        int read;
        while ((read = compressed.Read(buffer)) != 0)
        {
            if (expanded.Length + read > maximumBytes) return false;
            expanded.Write(buffer[..read]);
        }
        var bytes = expanded.ToArray();
        var count = bytes.Length;
        var headerEnd = Array.IndexOf(bytes, (byte)0, 0, count);
        if (headerEnd < 0) return false;
        var expectedHeader = Encoding.ASCII.GetBytes(type + " " +
            (count - headerEnd - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!bytes.AsSpan(0, headerEnd).SequenceEqual(expectedHeader)) return false;
        var hash = id.Length == 40 ? SHA1.HashData(bytes.AsSpan(0, count)) : SHA256.HashData(bytes.AsSpan(0, count));
        if (!Convert.ToHexString(hash).Equals(id, StringComparison.OrdinalIgnoreCase)) return false;
        contents = bytes[(headerEnd + 1)..count];
        return true;
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
