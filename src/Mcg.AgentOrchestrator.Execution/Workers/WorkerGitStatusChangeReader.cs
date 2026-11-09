namespace Mcg.AgentOrchestrator.Infrastructure;

// A short-status record proves a HEAD diff path only for a single-sided modification/deletion.
// Keep the original status text outside this reader, including its configuration-sensitive display.
internal static class WorkerGitStatusChangeReader
{
    internal static bool TryDeriveDiffNameOnly(
        string workingDirectory, string statusShortOutput, out string diffNameOnlyOutput)
    {
        diffNameOnlyOutput = string.Empty;
        // Submodule status and diff can use different ignore settings.
        if (File.Exists(Path.Combine(workingDirectory, ".gitmodules"))) return false;

        var paths = new List<string>();
        foreach (var line in statusShortOutput.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.Length < 4 || line[2] != ' ') return false;

            var columns = line[..2];
            // Both-sided edits may cancel against HEAD. Renames/copies, conflicts, type changes
            // and additions (which may be on an unborn HEAD) also need the original git calls.
            if (columns is not ("??" or "M " or "D " or " M" or " D")) return false;
            if (!TryReadPath(line[3..], out var path)) return false;
            if (columns != "??") paths.Add(path);
        }

        diffNameOnlyOutput = string.Join('\n', paths);
        return true;
    }

    private static bool TryReadPath(string token, out string path)
    {
        path = token;
        if (token.Length == 0) return false;
        if (token[0] != '"')
            return !token.Any(c => char.IsControl(c) || c is ' ' or '"' or '\\');

        if (token.Length < 3 || token[^1] != '"') return false;
        var escaped = false;
        for (var index = 1; index < token.Length - 1; index++)
        {
            var character = token[index];
            if (char.IsControl(character) || character == '"') return false;
            if (character != '\\') continue;
            escaped = true;
            if (++index >= token.Length - 1) return false;
            character = token[index];
            if (character is >= '0' and <= '3')
            {
                if (index + 2 >= token.Length - 1 ||
                    token[index + 1] is < '0' or > '7' || token[index + 2] is < '0' or > '7')
                    return false;
                index += 2;
            }
            else if (character is not ('a' or 'b' or 't' or 'n' or 'v' or 'f' or 'r' or '\\' or '"'))
                return false;
        }

        // Short status additionally quotes spaces; diff --name-only quotes only C-escaped paths.
        if (!escaped) path = token[1..^1];
        return true;
    }
}
