using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Resumable interpretation, not a selection/result memo: skipping old bytes requires their
// bounded decision text and parser state. Snapshot finalizes a copy, so a trailing line or
// incomplete UTF-8 character can still change meaning when more output arrives.
internal sealed class ProcessDecisionLogScan
{
    internal long Offset { get; private set; }
    internal string Identity { get; }
    internal long WriteTicks { get; set; }
    private byte[] pending = [];
    private Encoding? encoding;
    private ProcessDecisionLogSnapshot? snapshot;
    internal ProcessDecisionLogScan(string identity) => Identity = identity;
    private const int MaxDecisionChars = VerificationTextBounds.MaxRetainedChars;
    private StringBuilder decision = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
    private StringBuilder workerResult = new StringBuilder(Math.Min(MaxDecisionChars, VerificationTextBounds.BoundThreshold));
    private int prefixRemaining = VerificationTextBounds.PreviewHeadChars;
    private bool inWorkerResult = false;
    private long decisionTruncatedChars = 0L;
    private StringBuilder lineBuffer = new StringBuilder();
    private StringBuilder retainedPrefix = new StringBuilder(VerificationTextBounds.BoundThreshold);
    private char[] tail = new char[VerificationTextBounds.PreviewTailChars];
    private int tailStart = 0;
    private int tailCount = 0;
    private long totalChars = 0L;
    private bool previousWasCarriageReturn = false;


    private void ProcessDecisionLine()
    {
        var line = lineBuffer.ToString();
        lineBuffer.Clear();
        var containsFinalOutput = ContainsCodexFinalOutput(line);
        var normalized = NormalizeWorkerResultMarker(line);
        var isOpener = IsWorkerResultOpener(normalized);
        var isEndMarker = !isOpener && IsWorkerResultEndMarker(normalized);
        var isWorkerResultLine = isOpener || inWorkerResult || isEndMarker;
        var isDecisionContent =
            isOpener || isEndMarker || inWorkerResult || IsDecisionSignificantLine(line, containsFinalOutput);

        if (prefixRemaining > 0)
        {
            var take = Math.Min(prefixRemaining, line.Length);
            if (!isDecisionContent)
            {
                decisionTruncatedChars += AppendDecisionLine(decision, line[..take], MaxDecisionChars);
            }

            prefixRemaining -= take;
        }

        if (isOpener)
        {
            inWorkerResult = true;
        }

        if (isDecisionContent)
        {
            decisionTruncatedChars += AppendDecisionLine(
                isWorkerResultLine ? workerResult : decision,
                line,
                MaxDecisionChars);
        }

        if (isEndMarker)
        {
            inWorkerResult = false;
        }
    }


    internal void Read(Stream stream, long remaining = long.MaxValue)
    {
        var bytes = new byte[8192];
        int read;
        while (remaining > 0 && (read = stream.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining))) > 0)
        {
            snapshot = null;
            Offset += read;
            remaining -= read;
            Decode(pending.Concat(bytes.Take(read)).ToArray(), final: false);
        }
    }

    private void Decode(byte[] bytes, bool final)
    {
        var skip = 0;
        if (encoding is null)
        {
            if (bytes.Length < 4 && !final) { pending = bytes; return; }
            encoding = Encoding.UTF8;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = Encoding.UTF32; skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false); skip = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; skip = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        }
        var count = bytes.Length - skip;
        var held = 0;
        if (!final && encoding.CodePage == Encoding.UTF8.CodePage)
        {
            for (var i = Math.Max(skip, bytes.Length - 3); i < bytes.Length; i++)
                if (Rune.DecodeFromUtf8(bytes.AsSpan(i), out _, out _) == OperationStatus.NeedMoreData)
                { held = bytes.Length - i; break; }
        }
        else if (!final && encoding is UnicodeEncoding)
        {
            held = count % 2;
            if (count - held >= 2)
            {
                var unit = bytes.AsSpan(bytes.Length - held - 2, 2);
                var value = encoding.CodePage == Encoding.Unicode.CodePage
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(unit)
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(unit);
                if (char.IsHighSurrogate((char)value)) held += 2;
            }
        }
        else if (!final && encoding is UTF32Encoding) held = count % 4;
        pending = bytes.AsSpan(bytes.Length - held).ToArray();
        var chars = encoding.GetChars(bytes, skip, count - held);
        Feed(chars, chars.Length);
    }

    private void Feed(char[] buffer, int read)
    {
        if (retainedPrefix.Length < VerificationTextBounds.BoundThreshold)
        {
            retainedPrefix.Append(buffer, 0, Math.Min(read, VerificationTextBounds.BoundThreshold - retainedPrefix.Length));
        }

        for (var index = 0; index < read; index++)
        {
            var ch = buffer[index];
            if (tailCount < tail.Length)
            {
                tail[(tailStart + tailCount) % tail.Length] = ch;
                tailCount++;
            }
            else
            {
                tail[tailStart] = ch;
                tailStart = (tailStart + 1) % tail.Length;
            }

            if (ch == '\r')
            {
                ProcessDecisionLine();
                previousWasCarriageReturn = true;
            }
            else if (ch == '\n')
            {
                if (!previousWasCarriageReturn)
                {
                    ProcessDecisionLine();
                }

                previousWasCarriageReturn = false;
            }
            else
            {
                lineBuffer.Append(ch);
                previousWasCarriageReturn = false;
            }
        }

        totalChars += read;
    }

    internal ProcessDecisionLogSnapshot Snapshot(string path)
    {
        if (snapshot is not null) return snapshot;
        var copy = Copy();
        copy.Decode(pending, final: true);
        return snapshot = copy.Finish(path, Offset);
    }

    internal ProcessDecisionLogScan Copy()
    {
        var copy = (ProcessDecisionLogScan)MemberwiseClone();
        copy.decision = new StringBuilder(decision.ToString());
        copy.workerResult = new StringBuilder(workerResult.ToString());
        copy.lineBuffer = new StringBuilder(lineBuffer.ToString());
        copy.retainedPrefix = new StringBuilder(retainedPrefix.ToString());
        copy.tail = (char[])tail.Clone();
        return copy;
    }

    private ProcessDecisionLogSnapshot Finish(string path, long length)
    {
        if (lineBuffer.Length > 0)
        {
            ProcessDecisionLine();
        }

        var bounded = BuildBoundedText(retainedPrefix, tail, tailStart, tailCount, totalChars, path);
        var decisionProse = decision.ToString().TrimEnd();
        var workerResultText = workerResult.ToString().TrimEnd();
        string decisionText;
        if (workerResultText.Length == 0)
        {
            decisionText = decisionProse;
        }
        else
        {
            // WORKER_RESULT is contractual; surrounding prose is not. Preserve the captured block in the
            // same fixed-size snapshot and evict prose first. Raising the cap would only move the cliff;
            // completed-result decisions use the complete stdout artifact as their authority.
            var separatorLength = decisionProse.Length > 0 ? Environment.NewLine.Length : 0;
            var retainedProseLength = Math.Max(0, Math.Min(
                decisionProse.Length,
                MaxDecisionChars - workerResultText.Length - separatorLength));
            decisionTruncatedChars += decisionProse.Length - retainedProseLength;
            var retainedProse = decisionProse[..retainedProseLength].TrimEnd();
            decisionText = retainedProse.Length == 0
                ? workerResultText
                : retainedProse + Environment.NewLine + workerResultText;
        }

        return new ProcessDecisionLogSnapshot(
            length,
            ContainsCodexFinalOutput(decisionText),
            decisionText,
            bounded,
            decisionTruncatedChars);
    }

    private static string BuildBoundedText(
        StringBuilder retainedPrefix,
        char[] tail,
        int tailStart,
        int tailCount,
        long totalChars,
        string path)
    {
        if (totalChars <= VerificationTextBounds.BoundThreshold)
        {
            return retainedPrefix.ToString();
        }

        var head = retainedPrefix.ToString(0, VerificationTextBounds.PreviewHeadChars);
        var boundedTail = BuildTailText(tail, tailStart, tailCount);
        return VerificationTextBounds.BuildBoundedText(head, boundedTail, totalChars, path);
    }

    private static long AppendDecisionLine(StringBuilder target, string line, int maxChars)
    {
        if (string.IsNullOrEmpty(line))
        {
            return 0;
        }

        var attemptedChars = (long)line.Length + (target.Length > 0 ? Environment.NewLine.Length : 0);
        if (target.Length >= maxChars)
        {
            return attemptedChars;
        }

        if (target.Length > 0)
        {
            if (target.Length + Environment.NewLine.Length >= maxChars)
            {
                return attemptedChars;
            }

            target.AppendLine();
        }

        var remaining = maxChars - target.Length;
        var appendedChars = Math.Min(line.Length, remaining);
        target.Append(line, 0, appendedChars);
        return line.Length - appendedChars;
    }

    private static bool IsDecisionSignificantLine(string line, bool containsCodexFinalOutput)
    {
        return DispatchFailureClassifier.HasVerificationEvidenceInOutput(line, string.Empty) ||
            line.Contains("PLANNER_EVIDENCE_REQUEST:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN_INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("HUMAN INPUT:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("NO_CHANGE:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No-change rationale:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("No changes needed:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("index.lock", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("blocked on committing", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CreateProcessAsUserW", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("1312", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("specified logon session does not exist", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains(".git", StringComparison.OrdinalIgnoreCase) &&
             line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) ||
            line.Contains("sandbox-prep", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("[dispatch-host] terminating worker tree:", StringComparison.Ordinal) ||
            containsCodexFinalOutput ||
            IsProviderDecisionLine(line) ||
            line.Contains("Model fit:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Changed files:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Files changed:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProviderDecisionLine(string line)
    {
        return line.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("websocket", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("access token", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("model_not_found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("400", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkerResultOpener(string normalizedLine) =>
        string.Equals(normalizedLine.TrimEnd(':').Trim(), "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkerResultEndMarker(string normalizedLine) =>
        string.Equals(normalizedLine.Trim(), "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWorkerResultMarker(string text)
    {
        var trimmed = text.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var ch in trimmed)
        {
            if (ch is not ('#' or '*' or '`'))
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer, 0, length).Trim();
    }

    private static string BuildTailText(char[] tail, int tailStart, int tailCount)
    {
        if (tailCount == 0)
        {
            return string.Empty;
        }

        if (tailStart + tailCount <= tail.Length)
        {
            return new string(tail, tailStart, tailCount);
        }

        var suffixLength = tail.Length - tailStart;
        var builder = new StringBuilder(tailCount);
        builder.Append(tail, tailStart, suffixLength);
        builder.Append(tail, 0, tailCount - suffixLength);
        return builder.ToString();
    }

    private static bool ContainsCodexFinalOutput(string value)
    {
        return value.Contains("tokens used", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token usage", StringComparison.OrdinalIgnoreCase);
    }


    // Windows file id survives append and changes on replacement, even if the name/length
    // and tunneled creation time are identical. Other hosts use their creation identity.
    internal static string FileIdentity(string path)
    {
        if (!OperatingSystem.IsWindows()) return File.GetCreationTimeUtc(path).Ticks.ToString();
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var id = new byte[24]; // FILE_ID_INFO: volume serial plus 128-bit file id.
        if (!GetFileInformationByHandleEx(handle, 18, id, (uint)id.Length))
            throw new IOException("Cannot establish worker log identity.", Marshal.GetLastWin32Error());
        return Convert.ToHexString(id);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, [Out] byte[] info, uint size);
}
