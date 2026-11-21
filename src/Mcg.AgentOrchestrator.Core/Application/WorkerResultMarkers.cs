namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerResultMarkers
{
    internal static bool TryLocateBlock(string output, out int start, out int endMarkerStart, out int end)
    {
        start = endMarkerStart = end = -1;
        for (var offset = 0; offset < output.Length;)
        {
            var lineEnd = output.IndexOfAny(['\r', '\n'], offset);
            if (lineEnd < 0)
            {
                lineEnd = output.Length;
            }

            var line = output[offset..lineEnd];
            // Match the existing terminal-result convention: last opener, first following end.
            if (IsWorkerResultOpener(line))
            {
                start = offset;
                endMarkerStart = end = -1;
            }
            else if (start >= 0 && end < 0 && IsWorkerResultEndMarker(line))
            {
                endMarkerStart = offset;
                end = lineEnd;
            }

            offset = lineEnd;
            if (offset < output.Length && output[offset] == '\r')
            {
                offset++;
            }

            if (offset < output.Length && output[offset] == '\n')
            {
                offset++;
            }
        }

        return start >= 0 && end >= 0;
    }

    internal static bool IsWorkerResultOpener(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line).TrimEnd(':').Trim();
        return string.Equals(normalized, "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsWorkerResultEndMarker(string line)
    {
        var normalized = NormalizeWorkerResultMarker(line);
        return string.Equals(normalized, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeWorkerResultMarker(string text)
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
}
