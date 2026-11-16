using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Positive evidence only: the status delta is applied to a verified, immutable HEAD listing.
// No index or object writes. Unsupported conversion, metadata or filesystem shapes use git.
internal static class WorktreeDirtyTreeHasher
{
    private const int CacheCapacity = 8;
    private static readonly object CacheLock = new();
    private static readonly LinkedList<(string Id, Dictionary<string, Entry>? Entries)> Cache = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lazy<GitExecutable.GitInstallation?> Installation = new(
        () => GitExecutable.Recognize(HandleListProcessLauncher.ResolveApplication("git")));

    private sealed record Entry(string Mode, string Id);
    private sealed class Tree
    {
        internal readonly Dictionary<string, Tree> Directories = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Entry> Files = new(StringComparer.Ordinal);
    }

    internal static bool TryCompute(string root, string statusOutput, Func<string, string?> environment,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> run,
        Func<string, Func<string, string?>, string?>? resolveHeadTree, out string treeId)
    {
        treeId = string.Empty;
        try
        {
            if (resolveHeadTree is null || !TryParseStatus(statusOutput, out var touched) ||
                !GitRevisionReader.TryLocateRefStore(root, environment, out var git, out var common) ||
                !ConfigurationIsSafe(environment) ||
                !TryReadFileMode(Path.Combine(common, "config"), out var fileMode)) return false;
            var worktreeConfig = Path.Combine(git, "config.worktree");
            // Its precedence depends on extensions.worktreeConfig; leave that interpretation to git.
            if (File.Exists(worktreeConfig)) return false;
            var head = resolveHeadTree(root, environment);
            if (head is null || !IsId(head, head.Length) || !IndexIsSafe(Path.Combine(git, "index"), head.Length))
                return false;
            var entries = ReadHead(root, head.ToLowerInvariant(), run);
            if (entries is null || !AttributeSourcesAreSafe(root, common, environment, entries, touched.Keys))
                return false;

            // Conservative even on case-sensitive filesystems: status/path aliasing must be unambiguous.
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (entries.Keys.Any(path => !paths.Add(path))) return false;
            foreach (var path in touched.Keys)
                if (paths.Contains(path) && !entries.ContainsKey(path)) return false;
            paths.Clear();
            if (touched.Keys.Any(path => !paths.Add(path))) return false;

            var updated = new Dictionary<string, Entry>(entries, StringComparer.Ordinal);
            foreach (var (path, workingMode) in touched)
            {
                if (!OrdinaryParents(root, path)) return false;
                var fullPath = Path.Combine(root, path);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(fullPath); }
                catch (FileNotFoundException) { updated.Remove(path); continue; }
                catch (DirectoryNotFoundException) { updated.Remove(path); continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) { updated.Remove(path); continue; }
                // The oracle seeds its index from HEAD. A real-index-only path may be ignored
                // there, even though status reports it as tracked (forced add or staged rename).
                if (workingMode is not null && !entries.ContainsKey(path)) return false;
                var mode = fileMode ? workingMode : entries.GetValueOrDefault(path)?.Mode ?? "100644";
                if (mode is null)
                {
                    if (OperatingSystem.IsWindows()) return false;
                    mode = (File.GetUnixFileMode(fullPath) & UnixFileMode.UserExecute) != 0 ? "100755" : "100644";
                }
                if (mode is not ("100644" or "100755")) return false;
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var length = stream.Length;
                using var hash = NewHash(head.Length);
                hash.AppendData(Utf8.GetBytes("blob " + length.ToString(CultureInfo.InvariantCulture) + "\0"));
                var buffer = new byte[65536];
                long total = 0;
                int count;
                while ((count = stream.Read(buffer)) != 0)
                {
                    // No CR means text/eol/crlf/autocrlf conversion is the identity function.
                    if (buffer.AsSpan(0, count).Contains((byte)'\r')) return false;
                    hash.AppendData(buffer.AsSpan(0, count));
                    total += count;
                }
                if (total != length || stream.Length != length) return false;
                updated[path] = new(mode, Convert.ToHexStringLower(hash.GetHashAndReset()));
            }
            treeId = HashTree(BuildTree(updated), head.Length);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private static bool TryParseStatus(string output, out Dictionary<string, string?> touched)
    {
        touched = new(StringComparer.Ordinal);
        if (!output.EndsWith('\0')) return false;
        var records = output.Split('\0');
        for (var i = 0; i < records.Length - 1; i++)
        {
            var record = records[i];
            if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                var path = record[2..];
                if (!ValidPath(path)) return false;
                touched[path] = null;
                continue;
            }
            if (record.Length < 2 || record[1] != ' ' || record[0] is not ('1' or '2')) return false;
            var fields = record.Split(' ', record[0] == '1' ? 9 : 10);
            if (fields.Length != (record[0] == '1' ? 9 : 10) || fields[1].Length != 2 ||
                fields[1].Any(c => !".MADR".Contains(c)) || fields[2] != "N..." ||
                fields.Skip(3).Take(3).Any(mode => mode is not ("000000" or "100644" or "100755")) ||
                !IsId(fields[6], fields[6].Length) || !IsId(fields[7], fields[6].Length)) return false;
            var target = fields[^1];
            if (!ValidPath(target)) return false;
            touched[target] = fields[5];
            if (record[0] == '2')
            {
                if (++i >= records.Length - 1 || !ValidPath(records[i]) ||
                    fields[8].Length < 2 || fields[8][0] is not ('R' or 'C') ||
                    !fields[8][1..].All(char.IsAsciiDigit)) return false;
                touched.TryAdd(records[i], null);
            }
        }
        return touched.Count > 0;
    }

    private static bool ValidPath(string path) => path.Length > 0 && !path.Contains('\uFFFD') &&
        !path.Contains('\\') && !path.Contains(':') && !path.Contains('\0') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.TrimEnd(' ', '.').Equals(".git", StringComparison.OrdinalIgnoreCase) &&
            (!OperatingSystem.IsWindows() || part == part.TrimEnd(' ', '.')));

    private static bool IsId(string id, int length) => length is 40 or 64 && id.Length == length && id.All(Uri.IsHexDigit);

    private static Dictionary<string, Entry>? ReadHead(string root, string id,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> run)
    {
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
                if (node.Value.Id == id)
                {
                    Cache.Remove(node);
                    Cache.AddFirst(node);
                    return node.Value.Entries;
                }
        }
        var listing = run(root, new() { ["GIT_OPTIONAL_LOCKS"] = "0" },
            ["ls-tree", "-r", "-t", "-z", "--full-tree", id]);
        if (listing.ExitCode != 0 || listing.TimedOut) return null;
        Dictionary<string, Entry>? entries = ParseHead(listing.Output, id);
        lock (CacheLock)
        {
            // Concurrent fills for one immutable tree are equivalent.
            var existing = Cache.First;
            while (existing is not null && existing.Value.Id != id) existing = existing.Next;
            if (existing is not null) Cache.Remove(existing);
            Cache.AddFirst((id, entries));
            if (Cache.Count > CacheCapacity) Cache.RemoveLast();
        }
        return entries;
    }

    private static Dictionary<string, Entry>? ParseHead(string output, string id)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        if (output.Length != 0 && !output.EndsWith('\0')) return null;
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) return null;
            var fields = record[..tab].Split(' ');
            var path = record[(tab + 1)..];
            if (fields.Length != 3 || !ValidPath(path) || !IsId(fields[2], id.Length)) return null;
            if (fields[0] == "040000" && fields[1] == "tree")
            {
                if (!directories.TryAdd(path, fields[2].ToLowerInvariant())) return null;
            }
            else if (fields[0] is "100644" or "100755" && fields[1] == "blob")
            {
                if (!entries.TryAdd(path, new(fields[0], fields[2].ToLowerInvariant()))) return null;
            }
            else return null; // Includes symlinks and gitlinks, even if unchanged.
        }
        var tree = BuildTree(entries);
        var actualDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        if (HashTree(tree, id.Length, "", actualDirectories) != id || actualDirectories.Count != directories.Count ||
            directories.Any(pair => actualDirectories.GetValueOrDefault(pair.Key) != pair.Value)) return null;
        return entries;
    }

    private static Tree BuildTree(Dictionary<string, Entry> entries)
    {
        var root = new Tree();
        foreach (var (path, entry) in entries)
        {
            var parts = path.Split('/');
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (node.Files.ContainsKey(part)) throw new InvalidDataException();
                if (!node.Directories.TryGetValue(part, out var child)) node.Directories[part] = child = new();
                node = child;
            }
            if (node.Directories.ContainsKey(parts[^1])) throw new InvalidDataException();
            node.Files.Add(parts[^1], entry);
        }
        return root;
    }

    private static string HashTree(Tree tree, int length, string prefix = "", Dictionary<string, string>? directories = null)
    {
        var entries = tree.Files.Select(pair => (Name: pair.Key, Entry: pair.Value, Sort: Utf8.GetBytes(pair.Key))).ToList();
        foreach (var (name, child) in tree.Directories)
        {
            var path = prefix + name;
            var id = HashTree(child, length, path + "/", directories);
            directories?.Add(path, id);
            entries.Add((name, new("40000", id), Utf8.GetBytes(name + "/")));
        }
        entries.Sort((left, right) => left.Sort.AsSpan().SequenceCompareTo(right.Sort));
        using var body = new MemoryStream();
        foreach (var entry in entries)
        {
            body.Write(Utf8.GetBytes(entry.Entry.Mode + " " + entry.Name + "\0"));
            body.Write(Convert.FromHexString(entry.Entry.Id));
        }
        using var hash = NewHash(length);
        hash.AppendData(Utf8.GetBytes("tree " + body.Length.ToString(CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static IncrementalHash NewHash(int length) =>
        IncrementalHash.CreateHash(length == 40 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);

    private static bool IndexIsSafe(string path, int oidLength)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024 * 1024) return false;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var hashLength = oidLength / 2;
        var end = bytes.Length - hashLength;
        if (end < 12 || !bytes.AsSpan(0, 4).SequenceEqual("DIRC"u8) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)) != 2) return false;
        using var hash = NewHash(oidLength);
        hash.AppendData(bytes.AsSpan(0, end));
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(bytes.AsSpan(end))) return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
        var offset = 12;
        for (uint i = 0; i < count; i++)
        {
            var start = offset;
            var headerLength = 42 + hashLength;
            if (offset + headerLength > end) return false;
            var mode = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 24));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 40 + hashLength));
            // CE_VALID, extended flags (skip-worktree), nonzero merge stages, and nonregular entries.
            if ((flags & 0xf000) != 0 || mode is not (0x81a4 or 0x81ed)) return false;
            offset += headerLength;
            while (offset < end && bytes[offset] != 0) offset++;
            if (offset == end) return false;
            offset = start + ((offset - start + 8) & ~7);
            if (offset > end) return false;
        }
        while (offset < end)
        {
            if (end - offset < 8 || bytes[offset] is < (byte)'A' or > (byte)'Z') return false;
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4));
            if (size > end - offset - 8) return false;
            offset += 8 + (int)size;
        }
        return offset == end;
    }

    private static bool TryReadFileMode(string path, out bool mode)
    {
        mode = false;
        var found = false;
        var core = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Split(['#', ';'], 2)[0].Trim();
            if (line.StartsWith('[')) { core = line.Equals("[core]", StringComparison.OrdinalIgnoreCase); continue; }
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!core || parts.Length != 2 || !parts[0].Equals("filemode", StringComparison.OrdinalIgnoreCase)) continue;
            mode = parts[1].Length == 0 || parts[1].Equals("true", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[1] == "1";
            found = true;
        }
        return found;
    }

    private static bool ConfigurationIsSafe(Func<string, string?> environment)
    {
        if (environment("GIT_ATTR_SOURCE") is not null || environment("GIT_CONFIG") is not null) return false;
        var home = EffectiveHome(environment);
        if (string.IsNullOrEmpty(home)) return false;
        var sources = new List<string>();
        var global = environment("GIT_CONFIG_GLOBAL");
        if (global is not null) sources.Add(global);
        else
        {
            sources.Add(Path.Combine(home, ".gitconfig"));
            sources.Add(Path.Combine(environment("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "git", "config"));
        }
        if (!GitBooleanTrue(environment("GIT_CONFIG_NOSYSTEM")))
        {
            var system = environment("GIT_CONFIG_SYSTEM");
            if (system is null)
            {
                if (Installation.Value is not { } installation) return false;
                system = Path.Combine(installation.Root, "etc", "gitconfig");
            }
            sources.Add(system);
            if (OperatingSystem.IsWindows()) sources.Add(Path.Combine(environment("PROGRAMDATA") ??
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Git", "config"));
        }
        foreach (var path in sources)
        {
            if (path.Equals("NUL", StringComparison.OrdinalIgnoreCase) || path == "/dev/null") continue;
            if (!OptionalText(path, out var text)) return false;
            // Includes and attribute configuration (including attr.tree) require Git's parser.
            // Driver configuration itself has no effect; filter assignments are declined below.
            if (text.Contains("include", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("safecrlf", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("attr", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("sparsecheckout", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("worktree", StringComparison.OrdinalIgnoreCase) ||
                text.Contains('\\')) return false;
        }
        return true;
    }

    private static bool GitBooleanTrue(string? value) => value is not null &&
        (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private static string? EffectiveHome(Func<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(environment("HOME"))) return environment("HOME");
        if (!OperatingSystem.IsWindows()) return null;
        // Match GitExecutable's Windows wrapper rather than assuming USERPROFILE owns HOME.
        var homePath = environment("HOMEPATH");
        var candidate = environment("HOMEDRIVE") + homePath;
        if (!string.IsNullOrEmpty(homePath) && Directory.Exists(candidate) &&
            !candidate.Equals(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase)) return candidate;
        return environment("USERPROFILE");
    }

    private static bool AttributeSourcesAreSafe(string root, string common, Func<string, string?> environment,
        Dictionary<string, Entry> entries, IEnumerable<string> touched)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var home = EffectiveHome(environment);
        if (string.IsNullOrEmpty(home)) return false;
        sources.Add(Path.Combine(common, "info", "attributes"));
        sources.Add(Path.Combine(environment("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "git", "attributes"));
        if (!GitBooleanTrue(environment("GIT_ATTR_NOSYSTEM")))
        {
            if (Installation.Value is not { } installation) return false;
            sources.Add(Path.Combine(installation.Root, "etc", "gitattributes"));
        }
        var changed = touched.ToArray();
        if (changed.Any(path => Path.GetFileName(path).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)))
            return false;
        var relativeSources = new HashSet<string>(StringComparer.Ordinal);
        // The oracle starts from a fresh index: attributes can transform otherwise unchanged files.
        // Include ignored/untracked attribute files above HEAD paths, as well as those above changes.
        foreach (var path in entries.Keys.Concat(changed))
        {
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var relative = string.Join('/', parts.Take(i).Append(".gitattributes"));
                if (!relativeSources.Add(relative)) continue;
                if (!OrdinaryParents(root, relative)) return false;
                var source = Path.Combine(root, relative);
                // Git may read the index copy when the worktree attribute file is missing.
                if (entries.ContainsKey(relative) && !File.Exists(source)) return false;
                sources.Add(source);
            }
        }
        foreach (var source in sources)
        {
            if (!OptionalText(source, out var text)) return false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.Contains("filter", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("ident", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("working-tree-encoding", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[attr]", StringComparison.Ordinal)) return false;
            }
        }
        return true;
    }

    private static bool OptionalText(string path, out string text)
    {
        text = "";
        // Relative config paths are interpreted in the git child's working directory, which may
        // differ from this process. Never silently read a different source or treat it as absent.
        if (!Path.IsPathFullyQualified(path)) return false;
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
            text = Utf8.GetString(File.ReadAllBytes(path));
            return !text.Contains('\0');
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static bool OrdinaryParents(string root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/')[..^1])
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        return true;
    }
}
