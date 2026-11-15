using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ProcessLogReader
{
    private readonly Func<string, Stream> _openLogReadStream;
    private readonly Dictionary<ProcessDecisionLogCacheKey, ProcessDecisionLogScan> _decisionLogCache = [];
    private readonly object _decisionLogCacheGate = new();

    internal ProcessLogReader(Func<string, Stream>? openLogReadStream = null)
    {
        _openLogReadStream = openLogReadStream ??
            (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }

    internal ProcessDecisionLogSnapshot ReadBestEffort(TaskProcessRecord processRecord, string path)
    {
        var key = new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, path, processRecord.StartedAt, processRecord.ProcessId);
        lock (_decisionLogCacheGate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    _decisionLogCache.Remove(key);
                    return new(0, false, string.Empty, string.Empty);
                }
                var identity = ProcessDecisionLogScan.FileIdentity(path);
                var length = new FileInfo(path).Length;
                var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
                _decisionLogCache.TryGetValue(key, out var scan);
                if (scan is null || scan.Identity != identity || length < scan.Offset ||
                    (length == scan.Offset && scan.WriteTicks != writeTicks))
                    scan = new ProcessDecisionLogScan(identity);
                if (length != scan.Offset)
                {
                    using var stream = _openLogReadStream(path);
                    scan = scan.Copy(); // Publish cursor/parser state only after a complete successful read.
                    if (stream.CanSeek) stream.Seek(scan.Offset, SeekOrigin.Begin);
                    else scan = new ProcessDecisionLogScan(identity);
                    scan.Read(stream, length - scan.Offset);
                }
                scan.WriteTicks = writeTicks;
                _decisionLogCache[key] = scan;
                return scan.Snapshot(path);
            }
            catch (IOException)
            {
                var message = $"[log locked at refresh — see {path}]";
                return new(SafeFileLength(path), false, message, message, ReadSucceeded: false);
            }
            catch (UnauthorizedAccessException)
            {
                var message = $"[log unreadable at refresh — see {path}]";
                return new(SafeFileLength(path), false, message, message, ReadSucceeded: false);
            }
        }
    }

    internal CompleteLogReadResult ReadComplete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new CompleteLogReadResult(null, "missing");
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return new CompleteLogReadResult(reader.ReadToEnd(), null);
        }
        catch (IOException)
        {
            return new CompleteLogReadResult(null, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new CompleteLogReadResult(null, "unreadable");
        }
    }

    internal static string NormalizeCompleteDecisionText(string content)
    {
        var hasBareCarriageReturn = false;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r' && (index + 1 == content.Length || content[index + 1] != '\n'))
            {
                hasBareCarriageReturn = true;
                break;
            }
        }

        if (!hasBareCarriageReturn)
        {
            return content;
        }

        var normalized = new StringBuilder(content.Length);
        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (ch == '\r' && (index + 1 == content.Length || content[index + 1] != '\n'))
            {
                normalized.Append(Environment.NewLine);
            }
            else
            {
                normalized.Append(ch);
            }
        }

        return normalized.ToString();
    }

    internal void Evict(TaskProcessRecord processRecord)
    {
        lock (_decisionLogCacheGate)
        {
            _decisionLogCache.Remove(new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, processRecord.StandardOutputPath, processRecord.StartedAt, processRecord.ProcessId));
            _decisionLogCache.Remove(new ProcessDecisionLogCacheKey(processRecord.ExitCodePath, processRecord.StandardErrorPath, processRecord.StartedAt, processRecord.ProcessId));
        }
    }

    internal static string ReadDecisionBestEffort(string path)
    {
        var reader = new ProcessLogReader();
        return reader.ReadFileBestEffort(path, SafeFileLength(path)).DecisionText;
    }

    internal static string ReadBoundedBestEffort(string path)
    {
        var reader = new ProcessLogReader();
        return reader.ReadFileBestEffort(path, SafeFileLength(path)).BoundedText;
    }

    internal static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private ProcessDecisionLogSnapshot ReadFileBestEffort(string path, long length)
    {
        if (!File.Exists(path))
        {
            return new ProcessDecisionLogSnapshot(length, false, string.Empty, string.Empty, ReadSucceeded: true);
        }

        try
        {
            using var stream = _openLogReadStream(path);
            var scan = new ProcessDecisionLogScan(string.Empty);
            scan.Read(stream);
            return scan.Snapshot(path);
        }
        catch (IOException)
        {
            var message = $"[log locked at refresh — see {path}]";
            return new ProcessDecisionLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
        catch (UnauthorizedAccessException)
        {
            var message = $"[log unreadable at refresh — see {path}]";
            return new ProcessDecisionLogSnapshot(length, false, message, message, ReadSucceeded: false);
        }
    }

    private sealed record ProcessDecisionLogCacheKey(string ProcessRecordKey, string Path, DateTimeOffset StartedAt, int ProcessId);
}

internal sealed record ProcessDecisionLogSnapshot(
    long Length,
    bool FinalOutputSeen,
    string DecisionText,
    string BoundedText,
    long DecisionTruncatedChars = 0,
    bool ReadSucceeded = true);

internal sealed record CompleteLogReadResult(string? Content, string? UnavailableReason);
