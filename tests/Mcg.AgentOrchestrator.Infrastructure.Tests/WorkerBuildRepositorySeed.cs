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

    // A rejection describes the first failed eligibility check. No writes have happened at that point.
    internal static string? WriteOrDescribeFallback(string root)
    {
        var date = DateTimeOffset.Now;
        if (!OperatingSystem.IsWindows()) return "The seed writer requires Windows.";
        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root).ToUpperInvariant())))
            return "The seed root does not support case-insensitive lookup.";
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return "The seed root is a reparse point.";

        var (executable, environment) = InfrastructureTestSupport.CapturePreparedGitEnvironment();
        var rejection = ReadGitInputs(executable, environment, out var templates);
        if (rejection is not null) return rejection;
        if (!TryReadIdentity("AUTHOR", date, out var author)) return "Unsupported Git author identity.";
        if (!TryReadIdentity("COMMITTER", date, out var committer)) return "Unsupported Git committer identity.";
        rejection = ReadSeedFiles(root, out var files);
        if (rejection is not null) return rejection;

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
        return null;
    }

    private static string? ReadSeedFiles(string root, out Dictionary<string, byte[]> files)
    {
        files = new(StringComparer.Ordinal);
        var hasRootIgnore = File.Exists(Path.Combine(root, ".gitignore"));
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
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    return "The seed contains a reparse point or device.";
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) return "The seed contains .git metadata.";
                if (name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)) return "The seed contains .gitattributes.";
                if (!Regex.IsMatch(name, @"^[A-Za-z0-9_. -]+\z") || name.EndsWith('.') || name.EndsWith(' '))
                    return "The seed contains a path outside the supported name format.";
                if (!paths.Add(relative)) return $"Case-insensitive path collision at '{relative}'.";

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var isIgnoreFile = name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase);
                if (isIgnoreFile && (relative != ".gitignore" || isDirectory))
                    return $"Unsupported .gitignore location or type at '{relative}'.";
                if (isDirectory)
                {
                    // The supported negation matches .orchestrator at any depth.
                    var reIncluded = hasRootIgnore && relative.Contains('/') &&
                        name.Equals(".orchestrator", StringComparison.OrdinalIgnoreCase);
                    if (ExcludedDirectories.Contains(name) && !reIncluded) continue;
                    pending.Push(entry);
                    continue;
                }

                var bytes = File.ReadAllBytes(entry);
                if (bytes.Contains((byte)'\r')) return $"Seed file '{relative}' contains carriage returns.";
                if (isIgnoreFile && !bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(ManifestRules)))
                    return "The seed .gitignore differs from the supported manifest rules.";
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
                return "The seed manifest or its containing directory has an unsupported layout.";
            var bytes = File.ReadAllBytes(manifest);
            if (bytes.Contains((byte)'\r')) return $"Seed file '{Manifest}' contains carriage returns.";
            files.Add(Manifest, bytes);
        }
        return files.Count == 0 ? "The seed has no files to stage." : null;
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

    internal static string? ReadGitInputs(string executable, IDictionary<string, string?> environment, out string templates)
    {
        templates = "";
        // Git for Windows honors TZ when formatting commit timestamps. DateTimeOffset.Now does not.
        var unsupportedVariable = environment.Keys.FirstOrDefault(
            key => key.Equals("TZ", StringComparison.OrdinalIgnoreCase) ||
                (key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) && !SupportedGitVariables.Contains(key)));
        if (unsupportedVariable is not null) return $"Unsupported Git environment variable '{unsupportedVariable}'.";
        string? Get(string name) => environment.TryGetValue(name, out var value) ? value : null;
        var installation = Path.GetDirectoryName(Path.GetDirectoryName(executable));
        var installRoot = Path.GetDirectoryName(installation);
        var home = Get("HOME");
        if (installRoot is null) return "The prepared Git executable has no recognized installation root.";
        if (string.IsNullOrEmpty(home)) return "The prepared Git environment has no HOME.";
        templates = Path.Combine(installation!, "share", "git-core", "templates");
        var xdg = Get("XDG_CONFIG_HOME");
        var gitHome = Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "git");
        var global = Get("GIT_CONFIG_GLOBAL");
        var system = Get("GIT_CONFIG_SYSTEM") ?? Path.Combine(installRoot, "etc", "gitconfig");
        var noSystem = Get("GIT_CONFIG_NOSYSTEM");
        var noSystemAttributes = Get("GIT_ATTR_NOSYSTEM");
        try
        {
            if (noSystem is not (null or "0" or "1")) return "Unsupported GIT_CONFIG_NOSYSTEM value.";
            if (noSystemAttributes is not (null or "0" or "1")) return "Unsupported GIT_ATTR_NOSYSTEM value.";
            if (!HasOrdinaryTemplates(templates)) return "The Git installation has missing or unsupported templates.";
            var rejection = global is null
                ? ReadConfigRejection(Path.Combine(home, ".gitconfig"), "HOME") ??
                    ReadConfigRejection(Path.Combine(gitHome, "config"), "XDG")
                : ReadConfigRejection(global, "GIT_CONFIG_GLOBAL");
            if (rejection is not null) return rejection;
            if (noSystem != "1")
            {
                // Git for Windows 2.24 and later use the installation's system config or GIT_CONFIG_SYSTEM.
                rejection = ReadConfigRejection(system, "system");
                if (rejection is not null) return rejection;
            }
            if (File.Exists(Path.Combine(gitHome, "ignore"))) return "The XDG Git ignore file is present.";
            if (!IsDiffOnlyAttributes(Path.Combine(gitHome, "attributes"))) return "Unsupported XDG Git attributes.";
            if (noSystemAttributes != "1")
            {
                if (!IsDiffOnlyAttributes(Path.Combine(installRoot, "etc", "gitattributes")))
                    return "Unsupported system Git attributes.";
                if (!IsDiffOnlyAttributes(Path.Combine(installation!, "etc", "gitattributes")))
                    return "Unsupported Git prefix attributes.";
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not read prepared Git inputs ({error.GetType().Name}).";
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

    private static string? ReadConfigRejection(string path, string source)
    {
        if (path.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathFullyQualified(path)) return $"The {source} Git configuration path is not fully qualified.";
        var section = "";
        foreach (var line in ReadGitLines(path))
        {
            if (Regex.IsMatch(line, """^\[[A-Za-z]+(?: "[^"\\]+")?\]\z"""))
            {
                section = line[1..^1].ToLowerInvariant();
                continue;
            }
            var setting = Regex.Match(line, """^([A-Za-z]+)\s*=\s*([^"\\]*|"[^"\\]*")\z""");
            if (!setting.Success) return $"Unsupported syntax in {source} Git configuration.";
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
                })) return $"Unsupported '{key}' setting in {source} Git configuration.";
        }
        return null;
    }

    private static IEnumerable<string> ReadGitLines(string path)
    {
        try
        {
            // Git accepts a UTF-8 BOM in config files. A non-UTF-8 BOM must not switch decoders.
            using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true,
                throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
            var lines = new List<string>();
            while (reader.ReadLine() is { } raw)
            {
                if (raw.Contains('\0')) throw new DecoderFallbackException("NUL byte in Git input.");
                var line = raw.Trim();
                if (line.Length > 0 && line[0] is not ('#' or ';')) lines.Add(line);
            }
            return lines;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }
}
