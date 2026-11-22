using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// The worker-build fixture's restricted parentless SHA-1 seed. Unknown Git inputs go to Git.
internal static class WorkerBuildRepositorySeed
{
    internal const string Name = "Worker build fixture";
    internal const string Email = "worker-build@fixture.invalid";
    internal const string Message = "fixture seed";
    internal const string Excludes = ".orchestrator/\n.orchestrator-context/\nbin/\nobj/\n";
    private const string ManifestRules = "!.orchestrator/\n.orchestrator/*\n!.orchestrator/acceptance-manifest.json\n";
    private const string Manifest = ".orchestrator/acceptance-manifest.json";
    private static readonly HashSet<string> ExcludedDirectories =
        new(Excludes.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedGitVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL",
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_SYSTEM", "GIT_ATTR_NOSYSTEM",
        "GIT_EDITOR", "GIT_PAGER", "GIT_ASKPASS", "GIT_TERMINAL_PROMPT", "GIT_OPTIONAL_LOCKS",
        // The real fixture helper removes repository-selection overrides.
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"
    };

    internal static bool TryWrite(string root)
    {
        var date = DateTimeOffset.Now;
        if (!OperatingSystem.IsWindows() ||
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root).ToUpperInvariant())) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            !TryReadGitInputs(out var templates) ||
            !TryReadIdentity("AUTHOR", date, out var author) ||
            !TryReadIdentity("COMMITTER", date, out var committer) ||
            !TryReadSeedFiles(root, out var files))
            return false;

        // All eligibility checks happen before initialization. Partial writes enter fixture cleanup.
        var writer = new FixtureRepositoryWriter(root);
        writer.Initialize(templates: templates);
        File.AppendAllText(Path.Combine(writer.GitDirectory, "config"), $"[user]\n\temail = {Email}\n\tname = {Name}\n");
        File.WriteAllText(Path.Combine(writer.GitDirectory, "info", "exclude"), Excludes);
        var blobs = files.ToDictionary(pair => pair.Key, pair => writer.WriteBlob(pair.Value), StringComparer.Ordinal);
        var head = writer.WriteCommit(writer.WriteTree(blobs), [], Message, author, committer);
        writer.WriteIndex(blobs);
        const string branch = "refs/heads/main";
        writer.WriteRef(branch, head);
        writer.AppendReflog(branch, null, head, committer, $"commit (initial): {Message}");
        writer.AppendReflog("HEAD", null, head, committer, $"commit (initial): {Message}");
        File.WriteAllText(Path.Combine(writer.GitDirectory, "COMMIT_EDITMSG"), Message + "\n");
        return true;
    }

    private static bool TryReadSeedFiles(string root, out Dictionary<string, byte[]> files)
    {
        files = new(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(name, @"^[A-Za-z0-9_. -]+\z") || name.EndsWith('.') || name.EndsWith(' ') ||
                    !paths.Add(relative))
                    return false;

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isIgnoreFile = name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase);
                if (isIgnoreFile && (relative != ".gitignore" || isDirectory)) return false;
                if (isDirectory)
                {
                    if (ExcludedDirectories.Contains(name)) continue;
                    pending.Push(entry);
                    continue;
                }

                var bytes = File.ReadAllBytes(entry);
                if (bytes.Contains((byte)'\r') ||
                    (isIgnoreFile && !bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(ManifestRules)))) return false;
                files.Add(relative, bytes);
            }
        }

        if (files.ContainsKey(".gitignore"))
        {
            // These exact fixture rules expose only the canonical manifest in the excluded root directory.
            var directory = Path.Combine(root, ".orchestrator");
            var manifest = Path.Combine(directory, "acceptance-manifest.json");
            if (!File.Exists(manifest) ||
                (File.GetAttributes(manifest) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
                !Directory.EnumerateDirectories(root, ".orchestrator").Contains(directory, StringComparer.Ordinal) ||
                !Directory.EnumerateFiles(directory, "acceptance-manifest.json").Contains(manifest, StringComparer.Ordinal) ||
                Path.Exists(Path.Combine(directory, ".gitignore")) || Path.Exists(Path.Combine(directory, ".gitattributes")) ||
                Path.Exists(Path.Combine(directory, ".git")))
                return false;
            var bytes = File.ReadAllBytes(manifest);
            if (bytes.Contains((byte)'\r')) return false;
            files.Add(Manifest, bytes);
        }
        return files.Count > 0;
    }

    private static bool TryReadIdentity(string kind, DateTimeOffset date, out FixtureRepositoryWriter.Identity identity)
    {
        var name = Environment.GetEnvironmentVariable($"GIT_{kind}_NAME") ?? Name;
        var email = Environment.GetEnvironmentVariable($"GIT_{kind}_EMAIL") ?? Email;
        identity = new(name, email, date);
        // Keep Git's identity overrides for ordinary ASCII identities, including the gate's.
        return Regex.IsMatch(name, @"^[A-Za-z0-9](?:[A-Za-z0-9 ._'-]*[A-Za-z0-9])?\z") &&
            Regex.IsMatch(email, @"^[A-Za-z0-9_.+-]+@[A-Za-z0-9.-]+\z");
    }

    private static bool TryReadGitInputs(out string templates)
    {
        templates = "";
        if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(
            key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && !SupportedGitVariables.Contains(key)))
            return false;
        var start = new ProcessStartInfo("git");
        GitExecutable.Prepare(start);
        string? Get(string name) => start.Environment.TryGetValue(name, out var value) ? value : null;
        var installation = Path.GetDirectoryName(Path.GetDirectoryName(start.FileName));
        var installRoot = Path.GetDirectoryName(installation);
        var home = Get("HOME");
        if (installRoot is null || string.IsNullOrEmpty(home)) return false;
        templates = Path.Combine(installation!, "share", "git-core", "templates");
        var xdg = Get("XDG_CONFIG_HOME");
        var gitHome = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "git");
        var global = Get("GIT_CONFIG_GLOBAL");
        var system = Get("GIT_CONFIG_SYSTEM") ?? Path.Combine(installRoot, "etc", "gitconfig");
        var noSystem = Get("GIT_CONFIG_NOSYSTEM");
        var noSystemAttributes = Get("GIT_ATTR_NOSYSTEM");
        var programData = Get("PROGRAMDATA") ??
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        try
        {
            return (noSystem is null or "0" or "1") && (noSystemAttributes is null or "0" or "1") &&
                HasOrdinaryTemplates(templates) &&
                (global is null ? IsSupportedConfig(Path.Combine(home, ".gitconfig")) &&
                    IsSupportedConfig(Path.Combine(gitHome, "config")) : IsSupportedConfig(global)) &&
                (noSystem == "1" ||
                    IsSupportedConfig(system) && IsSupportedConfig(Path.Combine(programData, "Git", "config"))) &&
                !File.Exists(Path.Combine(gitHome, "ignore")) &&
                IsDiffOnlyAttributes(Path.Combine(gitHome, "attributes")) &&
                (noSystemAttributes == "1" ||
                    IsDiffOnlyAttributes(Path.Combine(installRoot, "etc", "gitattributes")) &&
                    IsDiffOnlyAttributes(Path.Combine(installation!, "etc", "gitattributes")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasOrdinaryTemplates(string path) =>
        Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
        Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).All(entry =>
            (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0 &&
            Regex.IsMatch(Path.GetRelativePath(path, entry).Replace('\\', '/'),
                @"^(?:description|info(?:/exclude)?|hooks(?:/[A-Za-z0-9-]+\.sample)?)\z"));

    private static bool IsDiffOnlyAttributes(string path) => Path.IsPathFullyQualified(path) &&
        ReadGitLines(path).All(line => Regex.IsMatch(line, @"^\*\.[A-Za-z0-9]+\s+diff=astextplain\z"));

    private static bool IsSupportedConfig(string path)
    {
        if (path.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Path.IsPathFullyQualified(path)) return false;
        var section = "";
        foreach (var line in ReadGitLines(path))
        {
            if (Regex.IsMatch(line, """^\[[A-Za-z]+(?: "[^"\\]+")?\]\z"""))
            {
                section = line[1..^1].ToLowerInvariant();
                continue;
            }
            var setting = Regex.Match(line, """^([A-Za-z]+)\s*=\s*([^"\\]*|"[^"\\]*")\z""");
            if (!setting.Success) return false;
            var key = setting.Groups[1].Value.ToLowerInvariant();
            var value = setting.Groups[2].Value.Trim('"').ToLowerInvariant();
            if (!((section, key) switch
                {
                    ("core", "autocrlf") => value is "true" or "false" or "input",
                    ("core", "symlinks") => value == "false",
                    ("core", "fscache") or ("filter \"lfs\"", "required") => value is "true" or "false",
                    ("core", "editor") or ("init", "defaultbranch") or
                        ("user", "name" or "email") or ("http", "sslbackend") or ("pull", "rebase") or
                        ("credential", "helper") or ("credential \"https://dev.azure.com\"", "usehttppath") or
                        ("diff \"astextplain\"", "textconv") or ("filter \"lfs\"", "clean" or "smudge" or "process") => true,
                    _ => false
                })) return false;
        }
        return true;
    }

    private static IEnumerable<string> ReadGitLines(string path)
    {
        try { return File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && line[0] is not ('#' or ';')); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
}
