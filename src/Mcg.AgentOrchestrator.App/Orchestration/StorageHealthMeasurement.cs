using System.Diagnostics;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Shared registry-based measurement for maintenance and the explicit storage-health query.
internal static class StorageHealthMeasurement
{
    internal sealed record ClassRow(string Pattern, string Owner, string Writer, string Retention,
        double? RetentionAgeDays, long Bytes, long Files, long BudgetBytes, string Status);
    internal sealed record UnregisteredRow(string Name, long Bytes, long Files);
    internal sealed record Totals(long Bytes, long Files, int Classes, int Over, int Unregistered, long Unreadable, double ElapsedMs);
    internal sealed record Report(IReadOnlyList<ClassRow> Classes, IReadOnlyList<UnregisteredRow> Unregistered, Totals Totals);

    internal static Report Build(string orchestratorDirectory, IReadOnlyList<OrchestratorArtifactRegistry.Entry> entries)
    {
        var stopwatch = Stopwatch.StartNew();
        var sizes = entries.ToDictionary(e => e.Pattern, _ => (Bytes: 0L, Files: 0L, Present: false), StringComparer.OrdinalIgnoreCase);
        var unregistered = new List<UnregisteredRow>();
        long unreadable = 0;
        foreach (var path in TopLevelPaths(orchestratorDirectory, ref unreadable))
        {
            var name = Path.GetFileName(path);
            var entry = OrchestratorArtifactRegistry.Classify(name, entries);
            var size = Measure(path, ref unreadable);
            if (entry is null) unregistered.Add(new(name, size.Bytes, size.Files));
            else
            {
                var current = sizes[entry.Pattern];
                sizes[entry.Pattern] = (current.Bytes + size.Bytes, current.Files + size.Files, true);
            }
        }
        var classes = entries.Select(entry =>
        {
            var size = sizes[entry.Pattern];
            return new ClassRow(entry.Pattern, entry.Owner, entry.Writer.ToString(), entry.Retention.ToString(),
                entry.RetentionAge?.TotalDays, size.Bytes, size.Files, entry.BudgetBytes,
                !size.Present ? "absent" : size.Bytes > entry.BudgetBytes ? "over" : "ok");
        }).OrderByDescending(row => row.Bytes).ThenBy(row => row.Pattern, StringComparer.Ordinal).ToArray();
        var unknown = unregistered.OrderByDescending(row => row.Bytes).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        return new(classes, unknown, new(classes.Sum(row => row.Bytes) + unknown.Sum(row => row.Bytes),
            classes.Sum(row => row.Files) + unknown.Sum(row => row.Files), entries.Count,
            classes.Count(row => row.Status == "over"), unknown.Length, unreadable, stopwatch.Elapsed.TotalMilliseconds));
    }

    private static string[] TopLevelPaths(string directory, ref long unreadable)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return [];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; return []; }
        return Children(directory, ref unreadable);
    }

    private static (long Bytes, long Files) Measure(string path, ref long unreadable)
    {
        long bytes = 0;
        long files = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Children(current, ref unreadable)) pending.Push(child);
                }
                else { bytes += new FileInfo(current).Length; files++; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }
        return (bytes, files);
    }

    private static string[] Children(string directory, ref long unreadable)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory, "*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; return []; }
    }
}
