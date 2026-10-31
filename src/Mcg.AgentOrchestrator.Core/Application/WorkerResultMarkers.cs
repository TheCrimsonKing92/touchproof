namespace Mcg.AgentOrchestrator.Core;

internal static class WorkerResultMarkers
{
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
