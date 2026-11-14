using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaunchLockSummary
{
    private const string LaunchLockSummaryPrefix = "LAUNCH_LOCK_SUMMARY ";
    private const string ByClassPrefix = "LAUNCH_LOCK_BY_CLASS ";
    private const string BySitePrefix = "LAUNCH_LOCK_BY_SITE ";
    private const string ByImagePrefix = "LAUNCH_LOCK_BY_IMAGE ";

    internal static void Preserve(
        string output,
        string? stderr,
        string trxPath,
        string? attemptPrefix)
    {
        if (string.IsNullOrWhiteSpace(attemptPrefix))
        {
            return;
        }

        var attemptDirectory = Path.GetDirectoryName(attemptPrefix)
            ?? throw new InvalidOperationException("Acceptance attempt has no artifact directory.");
        var summaryPath = Path.Combine(
            attemptDirectory,
            Path.GetFileNameWithoutExtension(trxPath) + ".launch-lock-summary.txt");
        var selectedOutput = string.IsNullOrEmpty(stderr) ? output : stderr;
        string? summary = null;
        using var reader = new StringReader(selectedOutput);
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith(LaunchLockSummaryPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // A nested test host can finish before the lane host. Its teardown line appears first.
            summary = line;
        }

        if (summary is null)
        {
            // An invocation may reuse a lane artifact name after a failed test-host start.
            if (File.Exists(summaryPath))
            {
                File.Delete(summaryPath);
            }
            return;
        }

        Directory.CreateDirectory(attemptDirectory);
        var lines = new List<string> { summary };
        if (TryReadPid(summary, out var processId))
        {
            using var classReader = new StringReader(selectedOutput);
            while (classReader.ReadLine() is { } line)
                if ((line.StartsWith(ByClassPrefix, StringComparison.Ordinal) ||
                     line.StartsWith(BySitePrefix, StringComparison.Ordinal) ||
                     line.StartsWith(ByImagePrefix, StringComparison.Ordinal) ||
                     line.StartsWith("LAUNCH_LOCK_CPU_BY_CLASS ", StringComparison.Ordinal)) &&
                    TryReadPid(line, out var classProcessId) && classProcessId == processId)
                    lines.Add(line);
        }
        File.WriteAllText(summaryPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static bool TryReadPid(string line, out int processId)
    {
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (token.StartsWith("pid=", StringComparison.Ordinal))
                return int.TryParse(token.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out processId);
        processId = 0;
        return false;
    }
}
