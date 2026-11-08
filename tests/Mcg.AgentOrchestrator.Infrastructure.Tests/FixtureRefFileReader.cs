// Stateless reads for fixture-owned repositories, including their private bare origins.
// Unknown metadata and revision syntax retain Git's own resolution and failure behavior.
internal static class FixtureRefFileReader
{
    internal static string Read(string repository, string revision) =>
        TryRead(repository, revision, out var value)
            ? value
            : LandingExecutorTests.ReadGit(repository, "rev-parse", revision);

    internal static bool TryRead(string repository, string revision, out string value)
    {
        value = string.Empty;
        try
        {
            var git = Path.Combine(repository, ".git");
            if (File.Exists(git))
            {
                var pointer = File.ReadAllText(git).Trim();
                if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal)) return false;
                git = Path.GetFullPath(pointer[8..], repository);
            }
            else if (!Directory.Exists(git)) git = repository; // Private bare origin.

            var common = git;
            if (File.Exists(Path.Combine(git, "commondir")))
                common = Path.GetFullPath(File.ReadAllText(Path.Combine(git, "commondir")).Trim(), git);
            if (!File.Exists(Path.Combine(git, "HEAD")) || !Directory.Exists(Path.Combine(common, "objects")) ||
                Directory.Exists(Path.Combine(common, "reftable"))) return false;

            var packed = new Dictionary<string, string>(StringComparer.Ordinal);
            var packedPath = Path.Combine(common, "packed-refs");
            if (File.Exists(packedPath))
                foreach (var line in File.ReadLines(packedPath))
                {
                    if (line.Length == 0 || line[0] is '#' or '^') continue;
                    var separator = line.IndexOf(' ');
                    if (separator < 0 || !IsObjectId(line[..separator]) ||
                        !packed.TryAdd(line[(separator + 1)..], line[..separator])) return false;
                }

            if (revision == "HEAD")
            {
                var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                if (IsObjectId(head)) { value = head.ToLowerInvariant(); return true; }
                if (!head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return false;
                revision = head[5..];
            }
            if (!IsReference(revision)) return false;
            if (!revision.StartsWith("refs/", StringComparison.Ordinal))
            {
                // A shorthand is safe only when Git's other DWIM candidates are absent.
                foreach (var alternative in new[] { revision, "refs/" + revision, "refs/tags/" + revision,
                             "refs/remotes/" + revision, "refs/remotes/" + revision + "/HEAD" })
                    if (File.Exists(Path.Combine(common, alternative)) || packed.ContainsKey(alternative)) return false;
                revision = "refs/heads/" + revision;
            }
            if (!revision.StartsWith("refs/heads/", StringComparison.Ordinal)) return false;
            var loosePath = Path.Combine(common, revision);
            var contents = File.Exists(loosePath) ? File.ReadAllText(loosePath).Trim() : packed.GetValueOrDefault(revision);
            if (contents is null || !IsObjectId(contents)) return false; // Includes symref chains and unborn refs.
            value = contents.ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsReference(string value) =>
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal) &&
        !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "\\~^:?*[".Contains(character)) &&
        value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith('.') &&
                                    !part.EndsWith(".lock", StringComparison.Ordinal));
}
