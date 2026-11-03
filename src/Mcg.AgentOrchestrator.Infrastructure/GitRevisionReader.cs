using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Caches processes, never revisions. All production callers share this bounded owner.
internal sealed class GitRevisionReader : IDisposable
{
    internal static GitRevisionReader Shared { get; } = new();
    private readonly object gate = new();
    private readonly Dictionary<string, ReaderProcess> readers = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly int capacity;
    private readonly TimeSpan idleTime;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private long activity;
    private bool disposed;

    static GitRevisionReader() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();

    internal GitRevisionReader(int capacity = 16, TimeSpan? idleTime = null, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
        this.idleTime = idleTime ?? TimeSpan.FromSeconds(60);
        if (this.idleTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTime));
        this.clock = clock ?? TimeProvider.System;
        timer = this.clock.CreateTimer(_ => ExpireIdle(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // This is a fresh metadata read, independent of the cached revision-reader processes.
    internal static bool TryReadCurrentBranch(string worktree, out string? branch)
        => TryReadCurrentBranch(worktree, out branch, Environment.GetEnvironmentVariable);

    internal static bool TryReadCurrentBranch(string worktree, out string? branch, Func<string, string?> environment)
    {
        branch = null;
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
                if (environment(name) is not null) return false;

            var root = Path.GetFullPath(worktree);
            var gitDirectory = ResolveBranchGitDirectory(root);
            if (gitDirectory is null) return false;

            var headPath = Path.Combine(gitDirectory, "HEAD");
            // A legacy symlink HEAD points at the branch ref, not a detached object ID.
            if (new FileInfo(headPath).LinkTarget is not null) return false;
            var head = ReadBranchMetadataLine(headPath);
            if (head is null) return false;
            if (IsBranchObjectId(head)) return true;

            const string prefix = "ref: refs/heads/";
            if (!head.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var nameSuffix = head[prefix.Length..];
            if (!IsBranchRefName(nameSuffix)) return false;

            // Git follows symbolic refs. Let it resolve chains rather than returning their first name.
            var commonDirectory = gitDirectory;
            var commonPath = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonPath))
            {
                var common = ReadBranchMetadataLine(commonPath);
                if (common is null) return false;
                commonDirectory = Path.GetFullPath(common, gitDirectory);
                if (!Directory.Exists(commonDirectory)) return false;
            }
            var refPath = Path.Combine(commonDirectory, "refs", "heads", nameSuffix);
            if (Directory.Exists(refPath)) return false;
            if (File.Exists(refPath))
            {
                if (new FileInfo(refPath).LinkTarget is not null) return false;
                var reference = ReadBranchMetadataLine(refPath);
                if (reference is null || !IsBranchObjectId(reference)) return false;
            }

            // A missing loose ref is ordinary for both packed refs and unborn branches.
            branch = nameSuffix;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Positive evidence only: unsupported layouts/configuration retain the diagnostic git probe.
    internal static bool TryConfirmWorkTree(string directory)
        => TryConfirmWorkTree(directory, Environment.GetEnvironmentVariable);

    internal static bool TryConfirmWorkTree(string directory, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_CEILING_DIRECTORIES",
                "GIT_DISCOVERY_ACROSS_FILESYSTEM", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" })
                if (environment(name) is not null) return false;

            // Do not normalize away an alias or a metadata segment before inspecting it.
            if (directory.Split(['/', '\\']).Any(part => part == ".." ||
                part.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase))) return false;
            var start = Path.GetFullPath(directory);
            if (!Directory.Exists(start) || !HasOrdinaryDirectoryPath(start)) return false;
            if (start.Split(['/', '\\']).Any(part =>
                part.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase))) return false;

            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var entry = Path.Combine(current.FullName, ".git");
                if (WorkTreeMetadataEntryExists(entry))
                {
                    if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return false;
                    var gitDirectory = ResolveBranchGitDirectory(current.FullName);
                    if (gitDirectory is null || !HasOrdinaryDirectoryPath(gitDirectory)) return false;
                    var headPath = Path.Combine(gitDirectory, "HEAD");
                    if ((File.GetAttributes(headPath) & FileAttributes.ReparsePoint) != 0) return false;
                    var head = ReadBranchMetadataLine(headPath);
                    const string prefix = "ref: refs/heads/";
                    if (head is null || !(IsBranchObjectId(head) ||
                        head.StartsWith(prefix, StringComparison.Ordinal) && IsBranchRefName(head[prefix.Length..])))
                        return false;

                    var commonDirectory = gitDirectory;
                    var commonPath = Path.Combine(gitDirectory, "commondir");
                    if (WorkTreeMetadataEntryExists(commonPath))
                    {
                        if ((File.GetAttributes(commonPath) & FileAttributes.ReparsePoint) != 0) return false;
                        var common = ReadBranchMetadataLine(commonPath);
                        if (common is null) return false;
                        commonDirectory = Path.GetFullPath(common, gitDirectory);
                    }
                    if (!HasOrdinaryDirectoryPath(commonDirectory) ||
                        !Directory.Exists(Path.Combine(commonDirectory, "objects")) ||
                        !Directory.Exists(Path.Combine(commonDirectory, "refs")) ||
                        !HasConclusiveWorkTreeConfig(Path.Combine(commonDirectory, "config"))) return false;
                    var worktreeConfig = Path.Combine(gitDirectory, "config.worktree");
                    if (WorkTreeMetadataEntryExists(worktreeConfig) && !HasConclusiveWorkTreeConfig(worktreeConfig)) return false;

                    if (OperatingSystem.IsWindows())
                    {
                        using var identity = WindowsIdentity.GetCurrent();
                        foreach (var path in new[] { current.FullName, entry, gitDirectory })
                        {
                            var owner = Directory.Exists(path)
                                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
                            if (!Equals(owner, identity.User) && !(owner is SecurityIdentifier sid &&
                                sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) &&
                                new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))) return false;
                        }
                    }
                    return true;
                }
                // Git stops at a bare repository before looking for an enclosing checkout.
                if (WorkTreeMetadataEntryExists(Path.Combine(current.FullName, "HEAD"))) return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
        return false;
    }

    internal static bool TryResolveRootGitDirectory(string directory, out string gitDirectory)
        => TryResolveRootGitDirectory(directory, out gitDirectory, Environment.GetEnvironmentVariable);

    internal static bool TryResolveRootGitDirectory(
        string directory, out string gitDirectory, Func<string, string?> environment)
    {
        gitDirectory = string.Empty;
        try
        {
            if (!TryConfirmWorkTree(directory, environment)) return false;
            var entry = Path.Combine(directory, ".git");
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0) return false;

            gitDirectory = Path.GetFullPath(entry);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Positive evidence of absence, never a cached negative lookup. Uncertainty retains git.
    internal static bool TryConfirmBranchAbsent(string directory, string branch)
        => TryConfirmBranchAbsent(directory, branch, Environment.GetEnvironmentVariable);

    internal static bool TryConfirmBranchAbsent(string directory, string branch, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            if (!TryConfirmWorkTree(directory, environment) || !IsBranchRefName(branch)) return false;
            // rev-parse can resolve a describe-style name from its object suffix without any ref.
            if (HasDescribeObjectSuffix(branch)) return false;
            if (OperatingSystem.IsWindows() && branch.Split('/').Any(IsAmbiguousWindowsRefComponent)) return false;

            // Worktree confirmation permits nested directories; this proof requires the root entry.
            var root = Path.GetFullPath(directory);
            var entry = Path.Combine(root, ".git");
            if (!WorkTreeMetadataEntryExists(entry) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return false;
            var gitDirectory = ResolveBranchGitDirectory(root);
            if (gitDirectory is null || !HasOrdinaryDirectoryPath(gitDirectory)) return false;

            var commonDirectory = gitDirectory;
            var commonPath = Path.Combine(gitDirectory, "commondir");
            if (WorkTreeMetadataEntryExists(commonPath))
            {
                if ((File.GetAttributes(commonPath) & FileAttributes.ReparsePoint) != 0) return false;
                var common = ReadBranchMetadataLine(commonPath);
                if (common is null) return false;
                commonDirectory = Path.GetFullPath(common, gitDirectory);
            }
            if (!HasOrdinaryDirectoryPath(commonDirectory) ||
                WorkTreeMetadataEntryExists(Path.Combine(commonDirectory, "reftable")) ||
                WorkTreeMetadataEntryExists(Path.Combine(gitDirectory, "reftable")) ||
                !HasConclusiveWorkTreeConfig(Path.Combine(commonDirectory, "config"))) return false;

            var candidates = new[] { $"refs/heads/{branch}", $"refs/refs/heads/{branch}",
                $"refs/tags/refs/heads/{branch}", $"refs/heads/refs/heads/{branch}",
                $"refs/remotes/refs/heads/{branch}" };
            foreach (var candidate in candidates)
                if (!IsLooseBranchRefAbsent(commonDirectory, candidate)) return false;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var packedPath = Path.Combine(commonDirectory, "packed-refs");
            if (WorkTreeMetadataEntryExists(packedPath))
            {
                if ((File.GetAttributes(packedPath) & FileAttributes.ReparsePoint) != 0) return false;
                using var stream = File.OpenRead(packedPath);
                const int limit = 1024 * 1024;
                var bytes = new byte[limit + 1];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (count > limit) return false;
                var text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
                if (text.Contains('\0') || text.Contains('\r')) return false;
                var lines = text.Split('\n');
                if (!lines[0].StartsWith("# pack-refs with:", StringComparison.Ordinal)) return false;
                var canPeel = false;
                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (i == lines.Length - 1 && line.Length == 0) continue;
                    if (line.StartsWith('^'))
                    {
                        if (!canPeel || !IsBranchObjectId(line[1..])) return false;
                        canPeel = false;
                        continue;
                    }
                    var separator = line.IndexOf(' ');
                    if (separator < 0 || !IsBranchObjectId(line[..separator])) return false;
                    var name = line[(separator + 1)..];
                    if (!name.StartsWith("refs/", StringComparison.Ordinal) || !IsBranchRefName(name)) return false;
                    if (candidates.Any(candidate => name.Equals(candidate, comparison)) ||
                        name.Equals($"refs/remotes/refs/heads/{branch}/HEAD", comparison) ||
                        name.StartsWith($"refs/heads/{branch}/", comparison)) return false;
                    canPeel = true;
                }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool HasDescribeObjectSuffix(string branch)
    {
        var suffixStart = branch.Length;
        while (suffixStart > 0 && Uri.IsHexDigit(branch[suffixStart - 1])) suffixStart--;
        return suffixStart < branch.Length && suffixStart >= 2 &&
            branch[suffixStart - 2] == '-' && branch[suffixStart - 1] == 'g';
    }

    private static bool IsAmbiguousWindowsRefComponent(string component)
    {
        if (component != component.TrimEnd(' ', '.')) return true;
        var stem = component.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && stem[3] is >= '1' and <= '9' &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLooseBranchRefAbsent(string commonDirectory, string candidate)
    {
        var current = commonDirectory;
        var components = candidate.Split('/');
        for (var i = 0; i < components.Length; i++)
        {
            var next = Path.Combine(current, components[i]);
            // Also decline case aliases in Windows directories with case sensitivity enabled.
            if (OperatingSystem.IsWindows())
            {
                var inspected = 0;
                foreach (var child in Directory.EnumerateFileSystemEntries(current))
                {
                    var name = Path.GetFileName(child);
                    if (++inspected > 4096 || name.Equals(components[i], StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals(components[i], StringComparison.Ordinal)) return false;
                }
            }
            if (!WorkTreeMetadataEntryExists(next)) return true;
            var attributes = File.GetAttributes(next);
            if (i == components.Length - 1 || (attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0) return false;
            current = next;
        }
        return false;
    }

    private static string? ResolveBranchGitDirectory(string root)
    {
        var gitDirectory = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDirectory))
        {
            var pointer = ReadBranchMetadataLine(gitDirectory);
            if (pointer is null || !pointer.StartsWith("gitdir: ", StringComparison.Ordinal) || pointer.Length == 8)
                return null;
            gitDirectory = Path.GetFullPath(pointer[8..], root);
            if (!Directory.Exists(gitDirectory)) return null;
        }
        return gitDirectory;
    }

    private static bool HasOrdinaryDirectoryPath(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0 ||
                current.Name != current.Name.TrimEnd(' ', '.') ||
                OperatingSystem.IsWindows() && current.Name.Contains('~')) return false;
        return true;
    }

    private static bool WorkTreeMetadataEntryExists(string path)
    {
        // Unlike Exists, preserve access/I/O failures so the caller declines confirmation.
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool HasConclusiveWorkTreeConfig(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var stream = File.OpenRead(path);
        var bytes = new byte[65537];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 65536) return false;
        var text = new UTF8Encoding(false, true).GetString(bytes, 0, count);
        if (text.Contains('\0') || text.StartsWith('\uFEFF')) return false;
        var section = "";
        var repositoryFormatVersion = "0";
        var hasObjectFormat = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            // Git's config whitespace is ASCII; do not normalize malformed input into valid syntax.
            if (line.Any(c => c is not (' ' or '\t') && (char.IsControl(c) || char.IsWhiteSpace(c)))) return false;
            line = line.Trim(' ', '\t');
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            // Includes, quoted subsections/values and continuations require Git's parser.
            if (line.Contains('"') || line.Contains('\\')) return false;
            line = line.Split(['#', ';'], 2)[0].TrimEnd(' ', '\t');
            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) return false;
                section = line[1..^1];
                if (section.Length == 0 || !section.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                section = section.ToLowerInvariant();
                if (section is "include" or "includeif") return false;
                continue;
            }
            var separator = line.IndexOf('=');
            if (section.Length == 0 || separator < 1) return false;
            var key = line[..separator].TrimEnd(' ', '\t');
            var value = line[(separator + 1)..].Trim(' ', '\t');
            if (key.Length == 0 || !char.IsAsciiLetter(key[0]) ||
                !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
            key = key.ToLowerInvariant();
            if (section == "core")
            {
                var isBoolean = value.Length == 0 || value is "0" or "1" ||
                    value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("off", StringComparison.OrdinalIgnoreCase);
                // Git interprets config before answering rev-parse. Only confirm settings whose
                // values we validate; unknown keys (even in other sections) retain its probe.
                var supported = key switch
                {
                    "repositoryformatversion" => value is "0" or "1",
                    "bare" => value.Equals("false", StringComparison.OrdinalIgnoreCase),
                    "filemode" or "symlinks" or "ignorecase" => isBoolean,
                    "logallrefupdates" => isBoolean || value.Equals("always", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (!supported) return false;
                if (key == "repositoryformatversion") repositoryFormatVersion = value;
            }
            else if (section == "extensions")
            {
                if (key == "objectformat" && value is "sha1" or "sha256") hasObjectFormat = true;
                else if (!(key == "worktreeconfig" && (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("false", StringComparison.OrdinalIgnoreCase)))) return false;
            }
            else return false;
        }
        return !hasObjectFormat || repositoryFormatVersion == "1";
    }

    private static string? ReadBranchMetadataLine(string path)
    {
        // Bound malformed files; larger or unsupported representations use the git fallback.
        using var stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[4097];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > 4096) return null;
        var line = new UTF8Encoding(false, true).GetString(bytes[..count]);
        if (line.EndsWith("\r\n", StringComparison.Ordinal)) line = line[..^2];
        else if (line.EndsWith('\n')) line = line[..^1];
        return line.Length == 0 || line[0] == '\uFEFF' || line.IndexOfAny(['\r', '\n']) >= 0 ? null : line;
    }

    private static bool IsBranchObjectId(string line) =>
        line.Length is 40 or 64 && line.All(Uri.IsHexDigit);

    private static bool IsBranchRefName(string name)
    {
        if (name.Length == 0 || name == "@" || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.') ||
            char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]) ||
            name.Contains("//", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal) ||
            name.Contains("@{", StringComparison.Ordinal) ||
            name.Any(c => c <= ' ' || c == '\u007f' || "~^:?*[\\".Contains(c)))
            return false;
        return name.Split('/').All(part => !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    }

    internal bool TryResolve(string workingDirectory, string revision, out string objectId) =>
        TryResolve(workingDirectory, revision, GitCli.DefaultTimeoutMilliseconds, out objectId);

    internal bool TryResolve(string workingDirectory, string revision, int timeoutMilliseconds, out string objectId)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        ReaderProcess? reader = null;
        string? key = null;
        try
        {
            try
            {
                if (CanUseBatch(revision))
                {
                    key = NormalizeDirectory(workingDirectory);
                    reader = Acquire(key);
                    if (reader is not null)
                    {
                        var line = reader.Query(revision, Remaining(deadline));
                        if (TryParseObject(line, out objectId))
                        {
                            if (!IsAbbreviatedRevision(revision, objectId.Length)) return true;
                        }
                        // A normal miss stays usable, but ALWAYS goes through the one-shot below.
                        else if (line != revision + " missing") Discard(key, reader);
                    }
                }
            }
            catch (Exception ex) when (IsReaderFailure(ex))
            {
                if (reader is not null && key is not null) Discard(key, reader);
            }

            var result = GitCli.Run(workingDirectory, Remaining(deadline), "rev-parse", "--verify", revision);
            objectId = result.Succeeded ? result.Output.Trim() : string.Empty;
            return objectId.Length > 0;
        }
        finally
        {
            if (reader is not null)
            {
                lock (gate)
                {
                    reader.Active--;
                    reader.LastCompleted = clock.GetTimestamp();
                    reader.Activity = ++activity;
                    ScheduleExpiry();
                }
            }
        }
    }

    private static int Remaining(long deadline) => (int)Math.Clamp(deadline - Environment.TickCount64, 1, int.MaxValue);
    private static string NormalizeDirectory(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static bool CanUseBatch(string revision) =>
        !string.IsNullOrWhiteSpace(revision) && revision[0] != '-' &&
        !revision.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == ':') &&
        !revision.Contains("@{", StringComparison.Ordinal); // Config/reflog-dependent expressions use fresh Git state.

    private static bool IsAbbreviatedRevision(string revision, int objectIdLength)
    {
        // Git caches abbreviated-object enumeration in a long-lived process. Confirm abbreviated
        // results without assuming a hash length; full IDs remain on the persistent path.
        var suffix = revision.IndexOfAny(['^', '~', '@']);
        var basis = suffix < 0 ? revision : revision[..suffix];
        return basis.Length >= 4 && basis.Length < objectIdLength && basis.All(Uri.IsHexDigit);
    }

    private ReaderProcess? Acquire(string key)
    {
        lock (gate)
        {
            if (disposed) return null;
            if (!readers.TryGetValue(key, out var reader))
            {
                var gitDirectory = FindGitDirectory(key);
                if (gitDirectory is null) return null;
                if (readers.Count == capacity)
                {
                    var oldest = readers.MinBy(pair => pair.Value.Activity);
                    readers.Remove(oldest.Key);
                    oldest.Value.Terminate(); // May interrupt an active query; it will fall back.
                }
                reader = ReaderProcess.Start(gitDirectory);
                readers.Add(key, reader);
            }
            reader.Active++;
            reader.Activity = ++activity;
            return reader;
        }
    }

    private static string? FindGitDirectory(string workingDirectory)
    {
        // Repository overrides must retain GitCli's inherited-environment semantics. Likewise,
        // subdirectories and bare repositories use the authoritative one-shot discovery path.
        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
            "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
            if (Environment.GetEnvironmentVariable(name) is not null) return null;
        var dotGit = Path.Combine(workingDirectory, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;
        var text = File.ReadAllText(dotGit).Trim();
        return text.StartsWith("gitdir: ", StringComparison.Ordinal)
            ? Path.GetFullPath(text[8..], workingDirectory) : null;
    }

    private static bool TryParseObject(string? line, out string objectId)
    {
        objectId = string.Empty;
        if (line is null) return false;
        var fields = line.Split(' ');
        if (fields.Length != 3 || fields[0].Length == 0 || !fields[0].All(Uri.IsHexDigit) ||
            fields[1] is not ("commit" or "tree" or "blob" or "tag") ||
            !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 0)
            return false;
        objectId = fields[0];
        return true;
    }

    private void Discard(string key, ReaderProcess reader)
    {
        lock (gate)
        {
            if (readers.TryGetValue(key, out var current) && ReferenceEquals(current, reader)) readers.Remove(key);
            reader.Terminate();
        }
    }

    private void ExpireIdle()
    {
        lock (gate)
        {
            if (disposed) return;
            var now = clock.GetTimestamp();
            foreach (var pair in readers.Where(pair => pair.Value.Active == 0 &&
                clock.GetElapsedTime(pair.Value.LastCompleted, now) >= idleTime).ToArray())
            {
                readers.Remove(pair.Key);
                pair.Value.Terminate();
            }
            ScheduleExpiry();
        }
    }

    private void ScheduleExpiry()
    {
        if (disposed) return;
        var now = clock.GetTimestamp();
        var deadlines = readers.Values.Where(reader => reader.Active == 0)
            .Select(reader => idleTime - clock.GetElapsedTime(reader.LastCompleted, now))
            .ToArray();
        var due = deadlines.Length == 0 ? Timeout.InfiniteTimeSpan : deadlines.Min();
        timer.Change(deadlines.Length > 0 && due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    // Tests inspect/terminate only handles owned by their isolated reader, never the OS PID namespace.
    internal ReaderProcess? GetReader(string workingDirectory)
    {
        lock (gate) return readers.GetValueOrDefault(NormalizeDirectory(workingDirectory));
    }

    internal int RetainedCount { get { lock (gate) return readers.Count; } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
            foreach (var reader in readers.Values) reader.Terminate();
            readers.Clear();
        }
    }

    private static bool IsReaderFailure(Exception ex) => ex is IOException or InvalidOperationException or
        System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    internal sealed class ReaderProcess
    {
        private readonly Process process;
        private readonly string packedRefsPath;
        private readonly string packedRefsVersion;
        private readonly object terminationGate = new();
        private readonly BlockingCollection<Request> requests = new();
        private int stopped;
        private volatile bool exited;
        internal int Active;
        internal long Activity;
        internal long LastCompleted;
        internal bool HasExited => exited;

        private ReaderProcess(Process process, string packedRefsPath, string packedRefsVersion)
        {
            this.process = process;
            this.packedRefsPath = packedRefsPath;
            this.packedRefsVersion = packedRefsVersion;
        }

        internal static ReaderProcess Start(string gitDirectory)
        {
            var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
            var commonDirectory = File.Exists(commonDirectoryFile)
                ? Path.GetFullPath(File.ReadAllText(commonDirectoryFile).Trim(), gitDirectory) : gitDirectory;
            var packedRefsPath = Path.Combine(commonDirectory, "packed-refs");
            var packedRefsVersion = ReadPackedRefsVersion(packedRefsPath);
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            // GitCli applies the shared launch contract to the persistent reader too.
            foreach (var arg in new[] { "--git-dir=" + gitDirectory,
                "cat-file", "--batch-check" }) info.ArgumentList.Add(arg);
            GitCli.ApplyLaunchSettings(info, readCommand: true);
            var process = ProcessTreeGuiSuppression.Start(info);
            var reader = new ReaderProcess(process, packedRefsPath, packedRefsVersion);
            try
            {
                // Notify on the querying thread, preserving census/test identity and source frames.
                try { GitCli.LaunchObserver?.Invoke("cat-file"); }
                catch (Exception) { /* Telemetry must not change the lookup result. */ }
                new Thread(reader.DrainErrors) { IsBackground = true, Name = "git-revision-stderr" }.Start();
                new Thread(reader.Run) { IsBackground = true, Name = "git-revision-protocol" }.Start();
                return reader;
            }
            catch
            {
                reader.Terminate();
                process.Dispose();
                throw;
            }
        }

        internal string? Query(string revision, int timeoutMilliseconds)
        {
            if (Volatile.Read(ref stopped) != 0 || process.HasExited) return null;
            // cat-file retains packed refs across requests. Invalidate before trusting any answer
            // after their contents change, including same-size/timestamp replacements. Returning
            // null discards this process and confirms the query through fresh one-shot Git state.
            if (ReadPackedRefsVersion(packedRefsPath) != packedRefsVersion) return null;
            var request = new Request(revision);
            if (!requests.TryAdd(request)) return null;
            return request.Answer.Task.Wait(timeoutMilliseconds) && !process.HasExited ? request.Answer.Task.Result : null;
        }

        private static string ReadPackedRefsVersion(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return string.Empty; }
        }

        private void Run()
        {
            try
            {
                // One dedicated protocol thread serializes both writes and reads. Even a blocked
                // stdin write is covered by the caller's wait, without relying on the thread pool.
                foreach (var request in requests.GetConsumingEnumerable())
                {
                    string? line = null;
                    try
                    {
                        process.StandardInput.Write(request.Revision + "\n");
                        process.StandardInput.Flush();
                        line = process.StandardOutput.ReadLine();
                    }
                    finally { request.Answer.TrySetResult(line); }
                    if (line is null) break;
                }
            }
            catch (Exception ex) when (IsReaderFailure(ex)) { /* Query falls back on a null answer. */ }
            finally
            {
                Terminate();
                while (requests.TryTake(out var pending)) pending.Answer.TrySetResult(null);
                process.Dispose();
                requests.Dispose();
            }
        }

        private void DrainErrors()
        {
            try
            {
                var buffer = new char[4096];
                while (process.StandardError.Read(buffer, 0, buffer.Length) > 0) { }
            }
            catch (Exception ex) when (IsReaderFailure(ex)) { /* Closing a reader also closes its pipes. */ }
        }

        internal void Terminate()
        {
            lock (terminationGate)
            {
                if (Interlocked.Exchange(ref stopped, 1) != 0) return;
                requests.CompleteAdding();
                KillProcess();
            }
        }

        // Also lets real-repository tests kill the owned OS handle without first closing the cache entry.
        internal void KillProcess()
        {
            lock (terminationGate)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    exited = process.WaitForExit(PipeDrain.DefaultTimeoutMilliseconds);
                }
                catch (Exception ex) when (IsReaderFailure(ex)) { /* Already exited/disposed. */ }
            }
        }

        private sealed record Request(string Revision)
        {
            internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
