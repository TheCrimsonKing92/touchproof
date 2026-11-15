using System.Diagnostics;

internal static class WorkerBuildFixtureHolderReport
{
    internal static string Describe(int? workerProcessId, string root,
        Func<int, IReadOnlyList<int>> liveDescendantReader, Func<int, string>? processNameReader = null)
    {
        var header = Environment.NewLine + $"Holder report for root '{root}', workerProcessId={workerProcessId?.ToString() ?? "<none>"}" +
            " (reader-returned live descendants are diagnostic candidates; filesystem-handle ownership is unverified): ";
        if (workerProcessId is null)
            return header + "No worker process was started; descendants were not enumerated.";

        try
        {
            var processIds = liveDescendantReader(workerProcessId.Value).Distinct().Order().ToArray();
            if (processIds.Length == 0) return header + "No live descendant was found.";
            processNameReader ??= ReadProcessName;
            return header + string.Join("; ", processIds.Select(processId =>
            {
                string name;
                try { name = processNameReader(processId); }
                catch { name = "<unavailable>"; }
                return $"pid={processId} name={name}";
            }));
        }
        catch (Exception error)
        {
            // Diagnostics must preserve the cleanup exception, including failed enumeration.
            return header + $"live-descendant enumeration failed: {error.GetType().Name}: {error.Message}";
        }
    }

    private static string ReadProcessName(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.ProcessName;
    }
}
