namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorRelaunchHandoffLedger(string conductEventsLogPath)
{
    internal IReadOnlyList<DateTimeOffset> Read()
    {
        var handoffs = new List<DateTimeOffset>();
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(conductEventsLogPath))!;
            if (!Directory.Exists(directory)) return handoffs;
            var stem = Path.GetFileNameWithoutExtension(conductEventsLogPath);
            var extension = Path.GetExtension(conductEventsLogPath);
            var paths = Directory.EnumerateFiles(directory, stem + "-*" + extension).ToList();
            if (File.Exists(conductEventsLogPath)) paths.Add(conductEventsLogPath);
            foreach (var path in paths)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    // ConductorContinuitySupervisor.cs:517 emits the successor-ready LOOP_HANDOFF attempt= event;
                    // is appended to this conduct-events log. Scheduled/draining events prove no handoff.
                    if (line.Contains("LOOP_HANDOFF attempt=", StringComparison.Ordinal) &&
                        ConductEventsLogTail.ParseLine(line) is { } row &&
                        row.Detail.StartsWith("LOOP_HANDOFF attempt=", StringComparison.Ordinal)) handoffs.Add(row.At);
                }
            }
            return handoffs;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
