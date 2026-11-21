using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Writes SHA-1 loose blobs, nested trees, commits, loose refs, symbolic HEAD, reflogs,
/// working files and a checksummed version 2 index for regular files (100644).
/// Linked worktrees share loose objects and branch refs with the common directory.
/// Packs, packed refs, per-worktree refs other than HEAD, worktree configuration,
/// SHA-256, non-regular modes and index versions 3/4 or semantic extensions are unsupported.
/// </summary>
internal sealed class FixtureRepositoryWriter
{
    internal FixtureRepositoryWriter(string repository)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        DotGit = Path.Combine(Root, ".git");
        GitDirectory = File.Exists(DotGit) ? ReadDirectoryPointer(DotGit, "gitdir: ") : DotGit;
        var commonPointer = Path.Combine(GitDirectory, "commondir");
        IsLinkedWorktree = Path.Exists(commonPointer);
        if (GitDirectory != DotGit && !IsLinkedWorktree)
            throw new InvalidDataException("Unsupported separate fixture Git directory.");
        CommonDirectory = IsLinkedWorktree ? ReadDirectoryPointer(commonPointer) : GitDirectory;
    }

    internal string Root { get; }
    internal string DotGit { get; }
    internal string GitDirectory { get; }
    internal string CommonDirectory { get; }
    internal bool IsLinkedWorktree { get; }

    internal static string ReadDirectoryPointer(string file, string prefix = "")
    {
        var text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(file));
        if (text.EndsWith('\n')) text = text[..^1];
        if (text.EndsWith('\r')) text = text[..^1];
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid fixture Git directory pointer.");
        var path = text[prefix.Length..];
        if (path.Length == 0 || path != path.Trim() || path.Any(char.IsControl) || path.StartsWith('\uFEFF') ||
            Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Invalid fixture Git directory pointer.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Path.GetDirectoryName(file)!));
    }
    internal sealed record Identity(string Name, string Email, DateTimeOffset Date)
    {
        internal string Header => $"{Name} <{Email}> {Date.ToUnixTimeSeconds()} {Date.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "")}";
    }

    internal void Initialize(string branch = "main", string? templates = null)
    {
        ValidateRef("refs/heads/" + branch);
        if (Path.Exists(DotGit))
            throw new IOException("Fixture repository already exists.");
        Directory.CreateDirectory(GitDirectory);
        if (templates is not null) CopyDirectory(templates, GitDirectory);
        foreach (var path in new[] { "objects/info", "objects/pack", "refs/heads", "refs/tags", "info", "hooks" })
            Directory.CreateDirectory(Path.Combine(GitDirectory, path));
        File.WriteAllText(Path.Combine(GitDirectory, "HEAD"), $"ref: refs/heads/{branch}\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(GitDirectory, "config"),
            "[core]\n\trepositoryformatversion = 0\n\tfilemode = " + (OperatingSystem.IsWindows() ? "false" : "true") +
            "\n\tbare = false\n\tlogallrefupdates = true\n" +
            (OperatingSystem.IsWindows() ? "\tsymlinks = false\n\tignorecase = true\n" : ""), Encoding.ASCII);
    }

    internal string WriteBlob(byte[] bytes) => WriteObject("blob", bytes);
    internal static string HashBlob(byte[] bytes) => ObjectId(ObjectBytes("blob", bytes));

    internal string WriteTree(IReadOnlyDictionary<string, string> files)
    {
        foreach (var path in files.Keys) ValidatePath(path);
        return WriteDirectory(files);
    }

    private string WriteDirectory(IReadOnlyDictionary<string, string> files)
    {
        var entries = new List<(string Name, string Mode, string Id)>();
        foreach (var group in files.GroupBy(pair => pair.Key.Split('/')[0], StringComparer.Ordinal))
        {
            var name = group.Key;
            if (group.Any(pair => pair.Key == name))
            {
                if (group.Count() != 1) throw new InvalidDataException("Tree has a file/directory collision.");
                entries.Add((name, "100644", group.Single().Value));
            }
            else
                entries.Add((name, "40000", WriteDirectory(group.ToDictionary(
                    pair => pair.Key[(name.Length + 1)..], pair => pair.Value, StringComparer.Ordinal))));
        }
        using var body = new MemoryStream();
        foreach (var entry in entries.OrderBy(entry => entry.Name + (entry.Mode == "40000" ? "/" : ""), StringComparer.Ordinal))
        {
            body.Write(Encoding.UTF8.GetBytes($"{entry.Mode} {entry.Name}\0"));
            body.Write(Convert.FromHexString(entry.Id));
        }
        return WriteObject("tree", body.ToArray());
    }

    internal string WriteCommit(string tree, IReadOnlyList<string> parents, string message,
        Identity author, Identity committer)
    {
        ValidateId(tree);
        foreach (var parent in parents) ValidateId(parent);
        var body = new StringBuilder($"tree {tree}\n");
        foreach (var parent in parents) body.Append($"parent {parent}\n");
        body.Append($"author {author.Header}\ncommitter {committer.Header}\n\n{message.TrimEnd('\n')}\n");
        return WriteObject("commit", Encoding.UTF8.GetBytes(body.ToString()));
    }

    internal string WriteSnapshot(IReadOnlyDictionary<string, byte[]> files, IReadOnlyList<string> parents,
        string message, Identity identity)
    {
        var blobs = files.ToDictionary(pair => pair.Key, pair => WriteBlob(pair.Value), StringComparer.Ordinal);
        return WriteCommit(WriteTree(blobs), parents, message, identity, identity);
    }

    // Commits explicit regular fixture files whose worktree bytes are already their intended blob bytes.
    internal string CommitFiles(IEnumerable<string> paths, string message, Identity identity, bool allowEmpty = false)
    {
        var reference = HeadReference() ?? throw new InvalidDataException("Fixture commit requires a branch.");
        var previous = ReadRef(reference) ?? throw new InvalidDataException("Fixture commit requires a seeded branch.");
        var files = ReadCommitFiles(previous);
        if (File.Exists(Path.Combine(GitDirectory, "MERGE_HEAD")) ||
            File.Exists(Path.Combine(CommonDirectory, "info", "attributes")) ||
            files.Keys.Any(IsAttributesPath))
            throw new InvalidDataException("Fixture commit requires a plain checkout without Git attributes.");

        var changes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var config = File.ReadAllText(Path.Combine(CommonDirectory, "config"));
        foreach (var path in paths)
        {
            ValidatePath(path);
            var file = Path.Combine(Root, path);
            for (var directory = Path.GetDirectoryName(file)!; ; directory = Path.GetDirectoryName(directory)!)
            {
                if (File.Exists(Path.Combine(directory, ".gitattributes")) || IsAttributesPath(path))
                    throw new InvalidDataException("Fixture commit encountered Git attributes.");
                if (directory == Root) break;
            }
            var bytes = File.ReadAllBytes(file);
            if (bytes.Contains((byte)'\r') && !config.Contains("\tautocrlf = false\n", StringComparison.Ordinal))
                throw new InvalidDataException("Fixture commit requires disabled newline conversion for CR bytes.");
            changes.Add(path, bytes);
        }

        if (!allowEmpty && changes.All(change =>
            files.TryGetValue(change.Key, out var blob) && blob == HashBlob(change.Value)))
            throw new InvalidDataException("Fixture commit has no changes.");
        foreach (var (path, bytes) in changes)
            files[path] = WriteBlob(bytes);
        var head = WriteCommit(WriteTree(files), [previous], message, identity, identity);
        WriteIndex(files);
        WriteRef(reference, head);
        var subject = $"commit: {message}";
        AppendReflog(reference, previous, head, identity, subject);
        AppendReflog("HEAD", previous, head, identity, subject);
        AtomicWrite(Path.Combine(GitDirectory, "COMMIT_EDITMSG"), Encoding.UTF8.GetBytes(message + "\n"));
        return head;

        static bool IsAttributesPath(string path) =>
            Path.GetFileName(path).Equals(".gitattributes", StringComparison.OrdinalIgnoreCase);
    }

    internal void SelectTip(string branch, string commit)
    {
        ValidateRef("refs/heads/" + branch);
        var files = ReadCommitFiles(commit);
        foreach (var (path, id) in files)
        {
            var target = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, ReadObject(id, "blob"));
        }
        // Selecting a new tip removes only files tracked by the old index.
        if (TryReadIndex(out var previous))
            foreach (var path in previous.Keys.Except(files.Keys, StringComparer.Ordinal))
                File.Delete(Path.Combine(Root, path));
        WriteIndex(files);
        WriteRef("refs/heads/" + branch, commit);
        WriteHead(branch);
    }

    internal void WriteHead(string branch)
    {
        ValidateRef("refs/heads/" + branch);
        AtomicWrite(Path.Combine(GitDirectory, "HEAD"), Encoding.ASCII.GetBytes($"ref: refs/heads/{branch}\n"));
    }

    internal void WriteRef(string reference, string id)
    {
        ValidateRef(reference);
        ValidateId(id);
        AtomicWrite(Path.Combine(CommonDirectory, reference), Encoding.ASCII.GetBytes(id + "\n"));
    }

    internal void AppendReflog(string reference, string? oldId, string newId, Identity identity, string subject)
    {
        if (reference != "HEAD") ValidateRef(reference);
        var path = Path.Combine(reference == "HEAD" ? GitDirectory : CommonDirectory, "logs", reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, $"{oldId ?? new string('0', 40)} {newId} {identity.Header}{(subject.Length == 0 ? "" : "\t" + subject)}\n", new UTF8Encoding(false));
    }

    internal string? HeadReference()
    {
        var text = File.ReadAllText(Path.Combine(GitDirectory, "HEAD")).Trim();
        return text.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ? text[5..] : null;
    }

    internal string? ReadRef(string reference)
    {
        ValidateRef(reference);
        var path = Path.Combine(CommonDirectory, reference);
        if (!File.Exists(path)) return null;
        var id = File.ReadAllText(path).Trim();
        ValidateId(id);
        return id;
    }

    internal Dictionary<string, string> ReadCommitFiles(string commit)
    {
        var body = Encoding.UTF8.GetString(ReadObject(commit, "commit"));
        if (!body.StartsWith("tree ", StringComparison.Ordinal)) throw new InvalidDataException("Commit has no tree.");
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        ReadTree(body.Substring(5, 40), "", files);
        return files;
    }

    private void ReadTree(string id, string prefix, Dictionary<string, string> files)
    {
        var tree = ReadObject(id, "tree");
        var offset = 0;
        while (offset < tree.Length)
        {
            var space = Array.IndexOf(tree, (byte)' ', offset);
            var end = Array.IndexOf(tree, (byte)0, space + 1);
            if (space < offset || end < space || end + 21 > tree.Length) throw new InvalidDataException("Invalid tree.");
            var mode = Encoding.ASCII.GetString(tree, offset, space - offset);
            var name = prefix + Encoding.UTF8.GetString(tree, space + 1, end - space - 1);
            ValidatePath(name);
            var child = Convert.ToHexStringLower(tree.AsSpan(end + 1, 20));
            if (mode == "40000") ReadTree(child, name + "/", files);
            else if (mode == "100644") files.Add(name, child);
            else throw new InvalidDataException("Unsupported tree mode.");
            offset = end + 21;
        }
    }

    internal byte[] ReadObject(string id, string expectedType)
    {
        ValidateId(id);
        using var input = File.OpenRead(Path.Combine(CommonDirectory, "objects", id[..2], id[2..]));
        using var compressed = new ZLibStream(input, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        compressed.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0 || Encoding.ASCII.GetString(bytes, 0, end) != $"{expectedType} {bytes.Length - end - 1}" ||
            ObjectId(bytes) != id)
            throw new InvalidDataException("Invalid loose object.");
        return bytes[(end + 1)..];
    }

    private string WriteObject(string type, byte[] body)
    {
        var bytes = ObjectBytes(type, body);
        var id = ObjectId(bytes);
        var path = Path.Combine(CommonDirectory, "objects", id[..2], id[2..]);
        if (File.Exists(path)) return id;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Create(temporary))
            using (var compressed = new ZLibStream(output, CompressionLevel.Optimal)) compressed.Write(bytes);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { }
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return id;
    }

    private static byte[] ObjectBytes(string type, byte[] body) =>
        Encoding.ASCII.GetBytes($"{type} {body.Length}\0").Concat(body).ToArray();

    private static string ObjectId(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    internal void WriteIndex(IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, byte[]>? refreshedFiles = null)
    {
        using var body = new MemoryStream();
        body.Write("DIRC"u8);
        WriteUInt(body, 2);
        WriteUInt(body, checked((uint)files.Count));
        Span<byte> flags = stackalloc byte[2];
        foreach (var (path, id) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ValidatePath(path);
            ValidateId(id);
            var start = body.Position;
            var info = new FileInfo(Path.Combine(Root, path));
            var matches = (refreshedFiles is null || refreshedFiles.ContainsKey(path)) &&
                info.Exists && HashBlob(File.ReadAllBytes(info.FullName)) == id;
            WriteTime(body, matches ? info.CreationTimeUtc : DateTime.UnixEpoch);
            WriteTime(body, matches ? info.LastWriteTimeUtc : DateTime.UnixEpoch);
            WriteUInt(body, 0); // dev
            WriteUInt(body, 0); // ino
            WriteUInt(body, 0x81a4); // regular 0644
            WriteUInt(body, 0); // uid
            WriteUInt(body, 0); // gid
            WriteUInt(body, matches ? checked((uint)info.Length) : 0);
            body.Write(Convert.FromHexString(id));
            var name = Encoding.UTF8.GetBytes(path);
            BinaryPrimitives.WriteUInt16BigEndian(flags, (ushort)Math.Min(name.Length, 0xfff));
            body.Write(flags);
            body.Write(name);
            do { body.WriteByte(0); } while ((body.Position - start) % 8 != 0);
        }
        var bytes = body.ToArray();
        AtomicWrite(Path.Combine(GitDirectory, "index"), bytes.Concat(SHA1.HashData(bytes)).ToArray());
    }

    internal bool TryReadIndex(out Dictionary<string, string> files)
    {
        files = new(StringComparer.Ordinal);
        var path = Path.Combine(GitDirectory, "index");
        if (!File.Exists(path)) return true;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 32 || !bytes.AsSpan(0, 4).SequenceEqual("DIRC"u8) ||
            ReadUInt(bytes, 4) != 2 || !SHA1.HashData(bytes.AsSpan(0, bytes.Length - 20)).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - 20)))
            return false;
        var count = ReadUInt(bytes, 8);
        var offset = 12;
        for (var i = 0u; i < count; i++)
        {
            if (offset + 62 >= bytes.Length - 20 || ReadUInt(bytes, offset + 24) != 0x81a4) return false;
            var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 60, 2));
            if ((flags & 0xf000) != 0) return false; // stages, extended or assume-valid
            var end = Array.IndexOf(bytes, (byte)0, offset + 62);
            if (end < 0 || end >= bytes.Length - 20) return false;
            var name = Encoding.UTF8.GetString(bytes, offset + 62, end - offset - 62);
            ValidatePath(name);
            if (!files.TryAdd(name, Convert.ToHexStringLower(bytes.AsSpan(offset + 40, 20)))) return false;
            offset += ((end - offset + 1 + 7) / 8) * 8;
        }
        while (offset < bytes.Length - 20)
        {
            // Git's disposable cache-tree is safe to drop when rebuilding an index.
            if (offset + 8 > bytes.Length - 20 || Encoding.ASCII.GetString(bytes, offset, 4) != "TREE") return false;
            var size = ReadUInt(bytes, offset + 4);
            if (size > bytes.Length - 20 - offset - 8) return false;
            offset += 8 + (int)size;
        }
        return offset == bytes.Length - 20;
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lockPath = path + ".lock";
        // CreateNew respects a real git writer's lock. Never remove a lock we did not own.
        using (var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            try { stream.Write(bytes); }
            catch { stream.Dispose(); File.Delete(lockPath); throw; }
        }
        try { File.Move(lockPath, path, overwrite: true); }
        finally { if (File.Exists(lockPath)) File.Delete(lockPath); }
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)) ||
            path.Any(c => c < 32 || c > 126 || c is ':' or '"'))
            throw new InvalidDataException("Unsupported fixture path.");
    }

    internal static void ValidateRef(string reference)
    {
        if (!reference.StartsWith("refs/", StringComparison.Ordinal) || reference.EndsWith('/') ||
            reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal) ||
            reference.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)) ||
            reference.Any(c => c <= 32 || c >= 127 || "~^:?*[\\".Contains(c)))
            throw new InvalidDataException("Unsupported fixture ref.");
    }

    private static void ValidateId(string id)
    {
        if (id.Length != 40 || id.Any(c => !char.IsAsciiHexDigit(c)) || id != id.ToLowerInvariant())
            throw new InvalidDataException("Expected SHA-1 object id.");
    }

    private static uint ReadUInt(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    private static void WriteUInt(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
    private static void WriteTime(Stream stream, DateTime time)
    {
        var ticks = Math.Max(0, time.Ticks - DateTime.UnixEpoch.Ticks);
        WriteUInt(stream, unchecked((uint)(ticks / TimeSpan.TicksPerSecond)));
        WriteUInt(stream, (uint)(ticks % TimeSpan.TicksPerSecond * 100));
    }
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
