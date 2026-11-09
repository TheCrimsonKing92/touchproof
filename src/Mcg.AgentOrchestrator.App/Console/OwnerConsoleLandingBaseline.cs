using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Keeps the membership behind the scan count so a later stream delivery cannot add it again.
internal static class OwnerConsoleLandingBaseline
{
    internal static IReadOnlySet<string> Read(string directory, TimeProvider clock)
    {
        var goals = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return goals;
        string[] files;
        try { files = Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return goals; }
        var today = clock.GetLocalNow().Date;
        foreach (var file in files) ReadFile(file, today, clock.LocalTimeZone, goals);
        return goals;
    }

    private static void ReadFile(string file, DateTime today, TimeZoneInfo zone, HashSet<string> goals)
    {
        try
        {
            if (TimeZoneInfo.ConvertTime(new DateTimeOffset(File.GetLastWriteTimeUtc(file)), zone).Date < today) return;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!line.Contains("GoalLanded", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("eventType", out var type) || type.ValueKind != JsonValueKind.String ||
                        type.GetString() != "GoalLanded" ||
                        !root.TryGetProperty("goalId", out var id) || id.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(id.GetString()) ||
                        !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                        !timestamp.TryGetDateTimeOffset(out var at)) continue;
                    if (TimeZoneInfo.ConvertTime(at, zone).Date == today) goals.Add(id.GetString()!);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
