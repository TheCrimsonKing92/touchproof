namespace Mcg.AgentOrchestrator.Infrastructure;

// Answers only checkout-root path queries whose layout and local configuration are proved.
// The caller owns its Git invocation (including timeout, environment and error handling).
internal static class GitPathQuery
{
    internal static GitCli.GitResult Run(string directory, Func<GitCli.GitResult> fallback,
        params string[] arguments) => Run(directory, fallback, Environment.GetEnvironmentVariable, arguments);

    internal static GitCli.GitResult Run(string directory, Func<GitCli.GitResult> fallback,
        Func<string, string?> environment, params string[] arguments)
    {
        if (TryQuery(directory, arguments, environment, out var output))
            return new GitCli.GitResult(0, output + "\n", "", ProcessStarted: false);
        return fallback();
    }

    internal static bool TryQuery(string directory, IReadOnlyList<string> arguments,
        Func<string, string?> environment, out string output)
    {
        output = "";
        try
        {
            var absolute = arguments.Count > 0 && arguments[0] == "--path-format=absolute";
            var offset = absolute ? 1 : 0;
            if (arguments.Count <= offset) return false;
            var form = arguments[offset];
            var pathQuery = form == "--git-path";
            if (arguments.Count != offset + (pathQuery ? 2 : 1) ||
                form is not ("--show-toplevel" or "--git-dir" or "--git-common-dir" or "--git-path" or
                    "--is-inside-work-tree")) return false;
            // Do not guess how an override, config include, alias or subdirectory affects Git.
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS",
                "GIT_CONFIG_COUNT", "GIT_CONFIG", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES",
                "GIT_DISCOVERY_ACROSS_FILESYSTEM" })
                if (environment(name) is not null) return false;
            if (!GitRevisionReader.TryLocateRefStore(directory, environment,
                out var gitDirectory, out var commonDirectory, allowIdentityConfiguration: true)) return false;

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var localGit = Directory.Exists(Path.Combine(root, ".git"));
            if (localGit && gitDirectory != commonDirectory) return false;
            if (!localGit)
            {
                var pointer = GitRevisionReader.ReadBranchMetadataLine(Path.Combine(root, ".git"));
                if (pointer is null || !Path.IsPathFullyQualified(pointer[8..])) return false;
                if (gitDirectory != commonDirectory)
                {
                    // A linked worktree must belong to this common directory and point back here.
                    var back = GitRevisionReader.ReadBranchMetadataLine(Path.Combine(gitDirectory, "gitdir"));
                    if (back is null || Path.GetFullPath(back, gitDirectory) != Path.Combine(root, ".git") ||
                        Directory.GetParent(gitDirectory)?.Parent?.FullName != commonDirectory) return false;
                }
            }
            var git = localGit ? ".git" : gitDirectory;
            var common = localGit ? ".git" : commonDirectory;
            if (form == "--is-inside-work-tree") output = "true";
            else if (form == "--show-toplevel") output = root;
            else if (form == "--git-dir") output = git;
            else if (form == "--git-common-dir") output = common;
            else
            {
                var name = arguments[offset + 1];
                // The common-dir exceptions are intentionally enumerated, not inferred from names.
                var basis = name switch
                {
                    "objects" or "info/exclude" => common,
                    "index" or "HEAD" or "mcg-rematerialization" or
                        "rebase-merge" or "rebase-apply" or "MERGE_HEAD" or "CHERRY_PICK_HEAD" or "REVERT_HEAD" => git,
                    _ => null
                };
                if (basis is null) return false;
                output = Path.Combine(basis, name);
            }
            if (absolute && form != "--is-inside-work-tree") output = Path.GetFullPath(output, root);
            output = output.Replace(Path.DirectorySeparatorChar, '/');
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
