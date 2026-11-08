namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Fresh repository observations shared by worker build scope entry points.</summary>
internal static class WorkerBuildRepositoryFacts
{
    internal sealed record Identity(string? TopLevel, string CommonDirectory, string Head, string? Branch)
    {
        internal string RequireBranch() => Branch ?? throw new IOException(
            "Worker build scope Git symbolic-ref failed: HEAD is detached; worker build scope requires a checked-out branch.");
    }

    internal static Identity ReadIdentity(string root, bool includeTopLevel = false)
    {
        var arguments = new List<string> { "rev-parse" };
        if (includeTopLevel) arguments.Add("--show-toplevel");
        arguments.AddRange(["--path-format=absolute", "--git-common-dir", "HEAD", "--abbrev-ref=loose", "HEAD"]);
        var lines = Git(root, arguments.ToArray()).TrimEnd('\r', '\n').Split('\n');
        if (lines.Length != (includeTopLevel ? 4 : 3))
            throw new IOException("Worker build scope Git rev-parse returned malformed repository identity.");
        var offset = includeTopLevel ? 1 : 0;
        var branch = lines[offset + 2].Trim();
        return new(includeTopLevel ? lines[0].Trim() : null, lines[offset].Trim(), lines[offset + 1].Trim(),
            branch == "HEAD" ? null : branch);
    }

    internal static IReadOnlyList<string> ReadChangedPaths(string root, string baseline)
    {
        var paths = Git(root, "diff", "--name-only", "-z", "--no-renames", baseline, "--")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        var worktree = new List<string>();
        var index = new List<string>();
        var untracked = new List<string>();
        var status = Git(root, "status", "--porcelain=v2", "-z", "--no-renames", "--untracked-files=all");
        foreach (var record in status.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (record[0])
            {
                case '1':
                case 'u':
                    // Split only the fixed metadata prefix. The path may itself contain spaces or newlines.
                    var count = record[0] == '1' ? 9 : 11;
                    var fields = record.Split(' ', count);
                    if (fields.Length != count || fields[1].Length != 2 || fields[^1].Length == 0)
                        throw MalformedStatus();
                    var xy = fields[1];
                    // diff ignores untracked-only dirt inside a submodule; status reports it as .M.
                    var untrackedOnlySubmodule = fields[2] == "S..U";
                    if (xy[1] != '.' && !untrackedOnlySubmodule) worktree.Add(fields[^1]);
                    if (xy[0] != '.') index.Add(fields[^1]);
                    break;
                case '?':
                    if (!record.StartsWith("? ", StringComparison.Ordinal) || record.Length == 2)
                        throw MalformedStatus();
                    untracked.Add(record[2..]);
                    break;
                default:
                    // --no-renames suppresses type 2; no headers or ignored records were requested.
                    throw MalformedStatus();
            }
        }
        // Preserve the legacy first-seen order, including changes that cancel against HEAD.
        paths.AddRange(worktree);
        paths.AddRange(index);
        paths.AddRange(untracked);
        return paths;

        static IOException MalformedStatus() => new("Worker build scope Git status returned malformed porcelain v2.");
    }

    internal static string Git(string root, params string[] arguments)
    {
        var result = GitCli.RunForRepository(root, arguments);
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"Worker build scope Git {arguments[0]} failed (exit={result.ExitCode}): {result.Error.Trim()}");
        return result.Output;
    }
}
