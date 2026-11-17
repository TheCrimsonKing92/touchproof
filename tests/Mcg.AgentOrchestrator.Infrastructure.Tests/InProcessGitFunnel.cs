using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;
using static WorkerDispatchTestsSeededRepositoryFactory;

// Test-host-only controls. No product component reads these variables or this mutable scope.
internal static class InProcessGitFunnel
{
    internal const string DisableVariable = "MCG_TEST_GIT_FUNNEL_REAL";
    internal const string RecorderVariable = "MCG_TEST_GIT_FUNNEL_RECORD";
    private static readonly bool Disabled = Environment.GetEnvironmentVariable(DisableVariable) == "1";
    private static readonly string? RecordingPath = Environment.GetEnvironmentVariable(RecorderVariable);
    private static readonly object RecordingLock = new();
    private static readonly AsyncLocal<Scope?> CurrentScope = new();
    internal static bool RealGitOnly => Disabled || CurrentScope.Value?.RealOnly == true;
    internal static readonly string[] ServedShapes = ["init", "config-set", "add-all", "add-paths", "commit-message",
        "checkout-new", "switch-new", "branch-create", "update-ref", "rev-parse"];
    private static readonly HashSet<string> EnvironmentKeys = new(StringComparer.Ordinal)
    {
        "GIT_AUTHOR_DATE", "GIT_COMMITTER_DATE", "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL",
        "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_PAGER"
    };
    private static readonly HashSet<string> RecordedOptions = new(StringComparer.Ordinal)
    {
        "-c", "-C", "-m", "-b", "-A", "-q", "--quiet", "--all", "--local", "--get", "--list",
        "--initial-branch", "--allow-empty", "--verify", "--porcelain", "--stage", "--format", "--name-only", "-r"
    };

    internal static bool Eligible(bool hasSeamOrHook, bool hasCounter,
        IReadOnlyDictionary<string, string>? environment) =>
        !RealGitOnly && !hasSeamOrHook && !hasCounter &&
        (environment is null || environment.Count > 0 && environment.Keys.All(EnvironmentKeys.Contains));

    internal static Scope Observe(bool realOnly = false) => new(realOnly);
    internal sealed class Scope : IDisposable
    {
        private readonly Scope? previous;
        private readonly object sync = new();
        private readonly List<string> served = [];
        internal bool RealOnly { get; }
        internal string[] Served { get { lock (sync) return served.ToArray(); } }
        internal Scope(bool realOnly)
        {
            previous = CurrentScope.Value;
            RealOnly = realOnly || previous?.RealOnly == true;
            CurrentScope.Value = this;
        }
        internal void Record(string shape) { lock (sync) served.Add(shape); previous?.Record(shape); }
        public void Dispose() => CurrentScope.Value = previous;
    }

    internal static void RecordLaunch(IReadOnlyList<string> arguments) => Record("launched", arguments);
    private static void Record(string disposition, IReadOnlyList<string> arguments)
    {
        if (RecordingPath is null) return;
        var commandIndex = 0;
        while (commandIndex + 2 < arguments.Count && arguments[commandIndex] is "-c" or "-C") commandIndex += 2;
        var command = commandIndex < arguments.Count && Regex.IsMatch(arguments[commandIndex], "^[a-z][a-z-]*$")
            ? arguments[commandIndex] : "<other>";
        // Values following options remain masked even when a message happens to look like an option.
        var valueNext = false;
        var tokens = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var arg = arguments[index];
            var option = arg.Split('=')[0];
            if (index == commandIndex) { tokens.Add(command); valueNext = false; }
            else if (valueNext) { tokens.Add("<value>"); valueNext = false; }
            else if (RecordedOptions.Contains(option))
            {
                tokens.Add(option + (arg.Contains('=') ? "=<value>" : ""));
                valueNext = !arg.Contains('=') && option is "-c" or "-C" or "-m" or "-b" or "--initial-branch";
            }
            else tokens.Add("<value>");
        }
        lock (RecordingLock) File.AppendAllText(RecordingPath, $"{disposition}\t{command}\t{string.Join(' ', tokens)}\n");
    }

    internal static bool TryServe(string root, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment, Func<string> nativeGit, out GitProbeResult result)
    {
        result = null!;
        Action? action;
        Func<string>? readOutput;
        string output, error, shape;
        try
        {
            if (args.Count == 0 || !TryPlan(new(root), args, environment, nativeGit, out action, out output, out error, out shape, out readOutput))
                return false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
        {
            return false; // Planning has performed no writes.
        }
        var exit = 0;
        try { action!(); output = readOutput?.Invoke() ?? output; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            // A write may already be visible. Never retry a partially executed operation with git.
            exit = 128;
            output = "";
            error = "fatal: in-process fixture write failed: " + ex.Message + "\n";
        }
        CurrentScope.Value?.Record(shape);
        Record("served", args);
        result = new("git " + string.Join(' ', args) + " [fixture writer]", true, exit, output, error, false, false,
            EnvironmentContract: "served=fixture-writer; repositorySelection=unset", Arguments: args,
            RepositoryDirectory: Path.GetFullPath(root), StandardOutputByteCount: Encoding.UTF8.GetByteCount(output),
            StandardErrorByteCount: Encoding.UTF8.GetByteCount(error),
            Classification: exit == 0 ? GitProbeClassification.Success : GitProbeClassification.NonZeroExit,
            Observations: new(new(), new(), new()));
        return true;
    }

    private static bool TryPlan(FixtureRepositoryWriter writer, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? environment, Func<string> nativeGit,
        out Action? action, out string output, out string error, out string shape, out Func<string>? readOutput)
    {
        action = null; readOutput = null; output = error = shape = "";
        if (args[0] == "init")
        {
            // The installed templates and config format are platform-specific. Other hosts use git.
            if (!OperatingSystem.IsWindows() || !Directory.Exists(writer.Root) ||
                File.Exists(writer.GitDirectory) || Directory.Exists(writer.GitDirectory) ||
                Directory.EnumerateFileSystemEntries(writer.Root).Any(path =>
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) return false;
            var quiet = false;
            var branch = "master";
            for (var i = 1; i < args.Count; i++)
            {
                if (args[i] is "-q" or "--quiet") quiet = true;
                else if (args[i] is "-b" or "--initial-branch" && i + 1 < args.Count) branch = args[++i];
                else if (args[i].StartsWith("--initial-branch=", StringComparison.Ordinal)) branch = args[i][17..];
                else return false;
            }
            FixtureRepositoryWriter.ValidateRef("refs/heads/" + branch);
            if (!ValidBranch(branch)) return false;
            var install = Path.GetDirectoryName(Path.GetDirectoryName(nativeGit()));
            var templates = Path.Combine(install ?? "", "mingw64", "share", "git-core", "templates");
            if (!Directory.Exists(templates) || File.Exists(Path.Combine(templates, "config"))) return false;
            action = () => writer.Initialize(branch, templates);
            output = quiet ? "" : "Initialized empty Git repository in " + writer.GitDirectory.Replace('\\', '/') + "/\n";
            shape = "init";
            return true;
        }

        if (!SafeRepository(writer, out var config)) return false;
        if (args[0] == "config")
        {
            var offset = args.Count > 1 && args[1] == "--local" ? 2 : 1;
            if (args.Count != offset + 2) return false;
            var key = args[offset];
            var value = args[offset + 1];
            if (key is not ("user.name" or "user.email" or "commit.gpgsign") ||
                key == "commit.gpgsign" && value != "false" || !SimpleValue(value)) return false;
            var bytes = SetConfig(File.ReadAllText(Path.Combine(writer.GitDirectory, "config")), key, value);
            action = () => FixtureRepositoryWriter.AtomicWrite(Path.Combine(writer.GitDirectory, "config"), Encoding.UTF8.GetBytes(bytes));
            shape = "config-set";
            return true;
        }
        if (args[0] == "rev-parse")
        {
            var index = 1;
            while (index < args.Count && args[index] is "--verify" or "-q" or "--quiet") index++;
            if (index != args.Count - 1 || !GitRefFileResolver.TryResolve(writer.Root, args[index], out var id, _ => null)) return false;
            action = () => { }; output = id + "\n"; shape = "rev-parse";
            return true;
        }
        if (!writer.TryReadIndex(out var staged)) return false;
        if (args[0] == "add")
            return PlanAdd(writer, args, staged, out action, out shape);
        var reference = writer.HeadReference();
        if (reference is null) return false;
        var head = writer.ReadRef(reference);
        var previous = head is null ? new Dictionary<string, string>(StringComparer.Ordinal) : writer.ReadCommitFiles(head);

        if (args[0] == "commit")
            return PlanCommit(writer, args, environment, config, reference, head, previous, staged, out action, out output, out shape, out readOutput);
        if (args.Count == 3 && (args[0] == "checkout" && args[1] == "-b" || args[0] == "switch" && args[1] == "-c"))
        {
            var branch = args[2];
            if (!ValidBranch(branch)) return false;
            var nextRef = "refs/heads/" + branch;
            FixtureRepositoryWriter.ValidateRef(nextRef);
            if (RefExists(writer, nextRef) || !EqualFiles(staged, previous) ||
                staged.Any(pair => !File.Exists(Path.Combine(writer.Root, pair.Key)) ||
                    !writer.ReadObject(pair.Value, "blob").SequenceEqual(File.ReadAllBytes(Path.Combine(writer.Root, pair.Key))))) return false;
            if (!TryIdentity("COMMITTER", environment, config, out var who)) return false;
            action = () =>
            {
                if (head is not null)
                {
                    writer.WriteRef(nextRef, head);
                    writer.AppendReflog(nextRef, null, head, who, "branch: Created from HEAD");
                    writer.AppendReflog("HEAD", head, head, who, $"checkout: moving from {reference[11..]} to {branch}");
                }
                writer.WriteHead(branch);
            };
            error = $"Switched to a new branch '{branch}'\n";
            shape = args[0] == "checkout" ? "checkout-new" : "switch-new";
            return true;
        }
        if (args[0] == "branch" && args.Count is 2 or 3 && !args[1].StartsWith('-') && head is not null)
        {
            if (!ValidBranch(args[1])) return false;
            var target = "refs/heads/" + args[1];
            FixtureRepositoryWriter.ValidateRef(target);
            if (RefExists(writer, target) || !GitRefFileResolver.TryResolve(writer.Root, args.Count == 3 ? args[2] : "HEAD", out var id, _ => null)) return false;
            // Validate type and support only branch starts backed by a loose commit.
            writer.ReadObject(id, "commit");
            if (!TryIdentity("COMMITTER", environment, config, out var who)) return false;
            action = () =>
            {
                writer.WriteRef(target, id);
                writer.AppendReflog(target, null, id, who, "branch: Created from " + (args.Count == 3 ? args[2] : reference[11..]));
            };
            shape = "branch-create";
            return true;
        }
        if (args[0] == "update-ref" && args.Count is 3 or 4 && args[1].StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var target = args[1];
            FixtureRepositoryWriter.ValidateRef(target);
            if (!SafeLocation(writer, target)) return false;
            var old = writer.ReadRef(target);
            if (File.Exists(Path.Combine(writer.GitDirectory, "packed-refs")) ||
                args.Count == 4 && args[3] != (old ?? new string('0', 40))) return false;
            writer.ReadObject(args[2], "commit");
            if (!TryIdentity("COMMITTER", environment, config, out var who)) return false;
            action = () =>
            {
                writer.WriteRef(target, args[2]);
                if (old != args[2])
                {
                    writer.AppendReflog(target, old, args[2], who, "");
                    if (target == reference) writer.AppendReflog("HEAD", old, args[2], who, "");
                }
            };
            shape = "update-ref";
            return true;
        }
        return false;
    }

    private static bool SafeRepository(FixtureRepositoryWriter writer, out Dictionary<string, string> config)
    {
        config = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(writer.GitDirectory) || File.Exists(Path.Combine(writer.GitDirectory, "commondir")) ||
            File.Exists(Path.Combine(writer.GitDirectory, "objects", "info", "alternates")) ||
            File.Exists(Path.Combine(writer.GitDirectory, "info", "attributes")) ||
            File.Exists(Path.Combine(writer.GitDirectory, "shallow")) ||
            Directory.Exists(Path.Combine(writer.GitDirectory, "refs", "replace"))) return false;
        // Check the metadata we consume, not every historical loose object on every call.
        foreach (var name in new[] { "HEAD", "index", "config", "objects", "objects/pack", "refs", "refs/heads", "hooks", "info/exclude" })
            if (!SafeLocation(writer, name)) return false;
        var packs = Path.Combine(writer.GitDirectory, "objects", "pack");
        if (Directory.Exists(packs) && Directory.EnumerateFiles(packs, "*.pack").Any()) return false;
        var currentRef = writer.HeadReference();
        if (currentRef is not null && !SafeLocation(writer, currentRef)) return false;
        var hooks = Path.Combine(writer.GitDirectory, "hooks");
        if (Directory.Exists(hooks) && Directory.EnumerateFiles(hooks).Any(path => !path.EndsWith(".sample", StringComparison.Ordinal))) return false;
        var section = "";
        foreach (var line in File.ReadAllLines(Path.Combine(writer.GitDirectory, "config")))
        {
            var text = line.Trim();
            if (text.Length == 0 || text[0] is '#' or ';') continue;
            if (Regex.IsMatch(text, @"^\[[A-Za-z0-9-]+\]$")) { section = text[1..^1].ToLowerInvariant(); continue; }
            var equals = text.IndexOf('=');
            if (equals <= 0 || section.Length == 0) return false;
            var key = section + "." + text[..equals].Trim();
            var value = text[(equals + 1)..].Trim();
            if (!SimpleValue(value) || !config.TryAdd(key, value)) return false;
        }
        foreach (var (key, value) in config)
        {
            // Explicitly bound the semantics whose implementation this fixture owns.
            if (key.ToLowerInvariant() switch
                {
                    "core.repositoryformatversion" => value != "0",
                    "core.bare" or "core.filemode" or "core.symlinks" or "commit.gpgsign" or "core.autocrlf" => value != "false",
                    "core.logallrefupdates" or "core.ignorecase" => value != "true",
                    "user.name" or "user.email" => false,
                    _ => true
                }) return false;
        }
        return true;
    }

    private static bool PlanAdd(FixtureRepositoryWriter writer, IReadOnlyList<string> args, Dictionary<string, string> staged,
        out Action? action, out string shape)
    {
        action = null; shape = "";
        if (args.Count < 2) return false;
        var all = args.Count == 2 && args[1] is "-A" or "--all" or ".";
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(writer.Root);
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (path == writer.GitDirectory) continue;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                var relative = Path.GetRelativePath(writer.Root, path).Replace('\\', '/');
                if (Path.GetFileName(path) is ".gitignore" or ".gitattributes" or ".git") return false;
                FixtureRepositoryWriter.ValidatePath(relative);
                if (Directory.Exists(path)) pending.Push(path); else paths.Add(relative);
            }
        }
        var exclude = Path.Combine(writer.GitDirectory, "info", "exclude");
        if (File.Exists(exclude) && File.ReadAllLines(exclude).Any(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))) return false;
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count) return false;
        var chosen = all ? paths.ToArray() : args.Skip(1).ToArray();
        if (!all && chosen.Any(path => path.StartsWith('-') || !paths.Contains(path, StringComparer.Ordinal))) return false;
        var content = chosen.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(writer.Root, path)), StringComparer.Ordinal);
        action = () =>
        {
            if (all) staged.Clear();
            foreach (var (path, bytes) in content) staged[path] = writer.WriteBlob(bytes);
            writer.WriteIndex(staged);
        };
        shape = all ? "add-all" : "add-paths";
        return true;
    }

    private static bool PlanCommit(FixtureRepositoryWriter writer, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env, Dictionary<string, string> config, string reference, string? head,
        Dictionary<string, string> previous, Dictionary<string, string> staged,
        out Action? action, out string output, out string shape, out Func<string>? readOutput)
    {
        action = null; readOutput = null; output = shape = "";
        var messages = new List<string>();
        var allowEmpty = false; var quiet = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "-m" && i + 1 < args.Count) messages.Add(args[++i]);
            else if (args[i] == "--allow-empty") allowEmpty = true;
            else if (args[i] is "-q" or "--quiet") quiet = true;
            else return false;
        }
        if (messages.Count == 0 || messages.Any(message => message.Any(c => c < 32 || c > 126)) ||
            new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "SQUASH_MSG", "rebase-merge", "rebase-apply", "sequencer" }
                .Any(name => File.Exists(Path.Combine(writer.GitDirectory, name)) || Directory.Exists(Path.Combine(writer.GitDirectory, name))) ||
            previous.Keys.Concat(staged.Keys).Any(path => Path.GetFileName(path) == ".gitattributes") ||
            File.Exists(Path.Combine(writer.GitDirectory, "packed-refs"))) return false;
        var message = string.Join("\n\n", messages.Select(CleanMessage)).Trim('\n');
        if (message.Length == 0 || !allowEmpty && EqualFiles(previous, staged)) return false;
        if (!TryIdentity("AUTHOR", env, config, out var author) || !TryIdentity("COMMITTER", env, config, out var committer)) return false;
        var added = staged.Keys.Except(previous.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var removed = previous.Keys.Except(staged.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (added.Length > 0 && removed.Length > 0) return false; // rename detection
        var changed = previous.Keys.Union(staged.Keys, StringComparer.Ordinal)
            .Where(path => !previous.TryGetValue(path, out var old) || !staged.TryGetValue(path, out var next) || old != next).ToArray();
        var insertions = 0; var deletions = 0;
        foreach (var path in changed)
        {
            if (path.Any(c => c < 32 || c > 126 || c is '"' or '\\')) return false;
            for (var directory = Path.GetDirectoryName(Path.Combine(writer.Root, path)); directory is not null;
                 directory = directory == writer.Root ? null : Path.GetDirectoryName(directory))
                if (File.Exists(Path.Combine(directory, ".gitattributes"))) return false;
            var old = previous.TryGetValue(path, out var before) ? writer.ReadObject(before, "blob") : [];
            var next = staged.TryGetValue(path, out var after) ? writer.ReadObject(after, "blob") : [];
            if (!TryLineChange(old, next, out var plus, out var minus)) return false;
            // Git's break detection can report a rewrite instead of this line summary.
            if (before is not null && after is not null && Math.Max(old.Length, next.Length) >= 400 && minus > 0) return false;
            insertions += plus; deletions += minus;
        }
        var summary = "";
        if (changed.Length > 0)
        {
            summary = $" {changed.Length} file{(changed.Length == 1 ? "" : "s")} changed";
            if (insertions > 0) summary += $", {insertions} insertion{(insertions == 1 ? "" : "s")}(+)";
            if (deletions > 0) summary += $", {deletions} deletion{(deletions == 1 ? "" : "s")}(-)";
            if (insertions == 0 && deletions == 0) summary += ", 0 insertions(+), 0 deletions(-)";
            summary += "\n";
            foreach (var path in added) summary += $" create mode 100644 {path}\n";
            foreach (var path in removed) summary += $" delete mode 100644 {path}\n";
        }
        // The invocation owns its output buffer; object creation supplies the final abbreviation.
        var branch = reference[11..];
        var plannedSummary = summary;
        var captured = new StringBuilder();
        action = () =>
        {
            var tree = writer.WriteTree(staged);
            var id = writer.WriteCommit(tree, head is null ? [] : [head], message, author, committer);
            FixtureRepositoryWriter.AtomicWrite(Path.Combine(writer.GitDirectory, "COMMIT_EDITMSG"), Encoding.UTF8.GetBytes(message + "\n"));
            writer.WriteRef(reference, id);
            writer.AppendReflog(reference, head, id, committer, $"commit{(head is null ? " (initial)" : "")}: {message.Split('\n')[0]}");
            writer.AppendReflog("HEAD", head, id, committer, $"commit{(head is null ? " (initial)" : "")}: {message.Split('\n')[0]}");
            if (!quiet)
            {
                captured.Append($"[{branch}{(head is null ? " (root-commit)" : "")} {Abbreviate(writer, id)}] {message.Split('\n')[0]}\n");
                if (author.Name != committer.Name || author.Email != committer.Email)
                    captured.Append($" Author: {author.Name} <{author.Email}>\n");
                captured.Append(plannedSummary);
            }
        };
        readOutput = captured.ToString;
        shape = "commit-message";
        return true;
    }

    private static string Abbreviate(FixtureRepositoryWriter writer, string id)
    {
        var names = Directory.EnumerateFiles(Path.Combine(writer.GitDirectory, "objects", id[..2]))
            .Select(Path.GetFileName).Where(name => name is { Length: 38 }).Select(name => id[..2] + name).ToArray();
        var length = 7;
        while (length < 40 && names.Count(name => name.StartsWith(id[..length], StringComparison.Ordinal)) > 1) length++;
        return id[..length];
    }
    private static bool TryIdentity(string kind, IReadOnlyDictionary<string, string>? env,
        Dictionary<string, string> config, out FixtureRepositoryWriter.Identity identity)
    {
        identity = null!;
        string? Read(string key) => env is not null && env.TryGetValue(key, out var value) ? value : null;
        var name = Read($"GIT_{kind}_NAME") ?? config.GetValueOrDefault("user.name");
        var email = Read($"GIT_{kind}_EMAIL") ?? config.GetValueOrDefault("user.email");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) ||
            name != name.Trim() || email != email.Trim() || (name + email).Any(c => c < 32 || c > 126 || c is '<' or '>')) return false;
        var date = Read($"GIT_{kind}_DATE");
        var time = DateTimeOffset.Now;
        if (date is not null)
        {
            if (Regex.IsMatch(date, @"^@?\d+ [+-]\d{4}$"))
            {
                var parts = date.TrimStart('@').Split(' ');
                var offset = TimeSpan.FromMinutes(int.Parse(parts[1][1..3], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1][3..], CultureInfo.InvariantCulture));
                time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[0], CultureInfo.InvariantCulture)).ToOffset(parts[1][0] == '-' ? -offset : offset);
            }
            else if (!Regex.IsMatch(date, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?([+-]\d\d:\d\d|Z)$") ||
                     !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) return false;
        }
        identity = new(name, email, time);
        return true;
    }
    private static string CleanMessage(string message) => Regex.Replace(
        string.Join('\n', message.Split('\n').Select(line => line.TrimEnd(' ', '\t'))).Trim('\n'), "\n{3,}", "\n\n");
    private static bool EqualFiles(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var id) && id == pair.Value);
    private static bool RefExists(FixtureRepositoryWriter writer, string reference) =>
        !SafeLocation(writer, reference) || File.Exists(Path.Combine(writer.GitDirectory, reference)) || File.Exists(Path.Combine(writer.GitDirectory, "packed-refs"));
    private static bool SafeLocation(FixtureRepositoryWriter writer, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(writer.GitDirectory, relative));
        if (!path.StartsWith(writer.GitDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        while (true)
        {
            if (File.Exists(path + ".lock") || (File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            if (path == writer.GitDirectory) return true;
            path = Path.GetDirectoryName(path)!;
        }
    }
    private static bool SimpleValue(string value) => value.Length > 0 && value == value.Trim() &&
        value.All(c => c >= 32 && c < 127 && c is not ('"' or '\\' or '#' or ';'));
    private static bool ValidBranch(string branch) => branch.Length > 0 && !branch.StartsWith('-') && branch != "HEAD";
    private static string SetConfig(string text, string key, string value)
    {
        var parts = key.Split('.');
        var lines = text.TrimEnd('\r', '\n').Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var section = lines.FindIndex(line => line.Equals($"[{parts[0]}]", StringComparison.OrdinalIgnoreCase));
        if (section < 0) { lines.Add($"[{parts[0]}]"); lines.Add($"\t{parts[1]} = {value}"); }
        else
        {
            var end = section + 1;
            while (end < lines.Count && !lines[end].StartsWith('[')) end++;
            var existing = lines.FindIndex(section + 1, end - section - 1,
                line => line.TrimStart().StartsWith(parts[1] + " ", StringComparison.OrdinalIgnoreCase) || line.TrimStart().StartsWith(parts[1] + "=", StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) lines[existing] = $"\t{parts[1]} = {value}";
            else lines.Insert(end, $"\t{parts[1]} = {value}");
        }
        return string.Join('\n', lines) + "\n";
    }
    private static bool TryLineChange(byte[] before, byte[] after, out int plus, out int minus)
    {
        plus = minus = 0;
        if (before.Contains((byte)0) || after.Contains((byte)0)) return false;
        string[] Lines(byte[] bytes)
        {
            var decoder = new UTF8Encoding(false, true);
            var lines = new List<string>();
            var start = 0;
            for (var i = 0; i < bytes.Length; i++)
                if (bytes[i] == (byte)'\n')
                {
                    lines.Add(decoder.GetString(bytes, start, i - start + 1));
                    start = i + 1;
                }
            if (start < bytes.Length) lines.Add(decoder.GetString(bytes, start, bytes.Length - start));
            return lines.ToArray();
        }
        var a = Lines(before); var b = Lines(after);
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        plus = b.Length - prefix - suffix; minus = a.Length - prefix - suffix;
        // A single replacement hunk has an unambiguous shortstat, too.
        return plus == 0 || minus == 0 || plus == 1 && minus == 1;
    }
}
