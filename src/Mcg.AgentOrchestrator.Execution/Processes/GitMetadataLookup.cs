namespace Mcg.AgentOrchestrator.Infrastructure;

// Each answer is proved from current metadata. A decline belongs to the caller's normal Git path.
internal static class GitMetadataLookup
{
    internal static bool IsLookupShape(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2 && arguments[0] == "branch" && arguments[1] == "--show-current") return true;
        if (arguments.Count < 2 || arguments[0] != "rev-parse") return false;
        if (arguments[1] == "--verify") return arguments.Count == 3;
        var offset = arguments[1] == "--path-format=absolute" ? 2 : 1;
        if (arguments.Count <= offset) return false;
        return arguments[offset] == "--git-path" ? arguments.Count == offset + 2 :
            arguments.Count == offset + 1 && arguments[offset] is "--show-toplevel" or "--git-dir" or
                "--git-common-dir" or "--is-inside-work-tree";
    }

    internal static bool TryAnswer(string directory, IReadOnlyList<string> arguments,
        Func<string, string?> environment, out string output)
    {
        output = string.Empty;
        if (!IsLookupShape(arguments)) return false;
        var verify = arguments.Count == 3 && arguments[0] == "rev-parse" && arguments[1] == "--verify";
        var branch = arguments.Count == 2 && arguments[0] == "branch" && arguments[1] == "--show-current";
        try
        {
            // The path proof owns the twelve environment refusals and checkout-root layout policy.
            IReadOnlyList<string> pathArguments = verify || branch
                ? ["--git-common-dir"] : arguments.Skip(1).ToArray();
            if (!GitPathQuery.TryQuery(directory, pathArguments, environment, out var path, out _, out var common) ||
                PackedRefsOnly(common)) return false;

            if (verify)
            {
                if (!GitRefFileResolver.TryResolveFullReference(directory, arguments[2], out var id, environment))
                    return false;
                output = id + "\n";
                return true;
            }
            if (branch)
            {
                if (!GitRefFileResolver.TryResolveFullReference(directory, "HEAD", out _, environment) ||
                    !GitRevisionReader.TryReadCurrentBranch(directory, out var name, environment) ||
                    name is not null && !name.All(char.IsAscii)) return false;
                output = name is null ? string.Empty : name + "\n";
                return true;
            }
            // Git's native output encoding and canonical Windows path spelling must be proved.
            if (!path.All(char.IsAscii) || !HasExactDirectorySpelling(directory)) return false;
            output = path + "\n";
            return true;
        }
        catch (Exception)
        {
            output = string.Empty;
            return false;
        }
    }

    private static bool PackedRefsOnly(string common)
    {
        if (!GitLookupDirectoryProbe.EntryExists(Path.Combine(common, "packed-refs"))) return false;
        foreach (var family in new[] { "heads", "tags" })
        {
            var path = Path.Combine(common, "refs", family);
            if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint })
                .Any(file => !file.EndsWith(".lock", StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static bool HasExactDirectorySpelling(string directory)
    {
        if (!OperatingSystem.IsWindows()) return true;
        return GitWindowsDirectoryPath.HasExactSpelling(directory);
    }
}
