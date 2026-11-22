using System.IO.Compression;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class GoalOperationJournal
{
    private static IReadOnlyList<string> ReadJournalLines(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return SharedJsonlFile.ReadAllLines(path);

        using var input = File.OpenRead(path);
        return ReadCompressedLines(input);
    }

    private static IReadOnlyList<string>? ReadJournalLinesIfPresent(string path, bool strict)
    {
        if (!strict) return File.Exists(path) ? ReadJournalLines(path) : null;

        // Only a missing path is absence. Open directly so access and wrong-type errors
        // reach the lifecycle reader even when a readable sibling journal exists.
        var compressed = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        using var input = OpenStrictJournalIfPresent(path, compressed);
        if (input is null) return null;
        return compressed ? ReadCompleteCompressedLines(input, path) : ReadLines(input);
    }

    private static FileStream? OpenStrictJournalIfPresent(string path, bool compressed)
    {
        var share = compressed ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete;
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, share); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) when (HasMissingParentDirectory(path)) { return null; }
    }

    private static bool HasMissingParentDirectory(string path)
    {
        // Windows also reports DirectoryNotFoundException when an ancestor is a file.
        // Walk to the nearest existing ancestor to distinguish the two cases.
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            try { return File.GetAttributes(parent).HasFlag(FileAttributes.Directory); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        return true;
    }

    private static List<string> ReadCompressedLines(Stream input)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        return ReadLines(gzip);
    }

    private static List<string> ReadLines(Stream input)
    {
        using var reader = new StreamReader(input, leaveOpen: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    private static List<string> ReadCompleteCompressedLines(Stream input, string path)
    {
        // GZipStream can report EOF after a truncated member. It can only decode the
        // appended member's marker after validating the preceding gzip member(s).
        if (input.Length == 0) throw new InvalidDataException($"Empty compressed journal '{path}'.");
        var marker = $"\0{Guid.NewGuid():N}";
        using var withMarker = new GzipWithEndMarker(input, marker);
        var lines = ReadCompressedLines(withMarker);
        if (lines.Count == 0 || !lines[^1].EndsWith(marker, StringComparison.Ordinal))
            throw new InvalidDataException($"Incomplete compressed journal '{path}'.");

        var lastLine = lines[^1][..^marker.Length];
        if (lastLine.Length == 0)
            lines.RemoveAt(lines.Count - 1);
        else
            lines[^1] = lastLine;
        return lines;
    }

    private sealed class GzipWithEndMarker(Stream input, string marker) : Stream
    {
        private readonly byte[] _member = CompressMarker(marker);
        private int _offset;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            if (_offset == 0)
            {
                var read = input.Read(buffer);
                if (read != 0) return read;
            }

            var size = Math.Min(buffer.Length, _member.Length - _offset);
            _member.AsSpan(_offset, size).CopyTo(buffer);
            _offset += size;
            return size;
        }

        private static byte[] CompressMarker(string marker)
        {
            using var member = new MemoryStream();
            using (var gzip = new GZipStream(member, CompressionLevel.Fastest, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes(marker));
            return member.ToArray();
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
