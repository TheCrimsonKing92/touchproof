using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record MeasurementLogRecord(DateTimeOffset Timestamp, JsonElement Data);
internal sealed record MeasurementLogInput(IReadOnlyList<MeasurementLogRecord> Records, int Skipped, int Unavailable);

internal static class OwnerDigestMeasurementLogs
{
    internal static MeasurementLogInput Read(string directory, string pattern)
    {
        var records = new List<(MeasurementLogRecord Record, int File, int Line)>();
        var skipped = 0;
        var unavailable = 0;
        string[] files;
        try { files = Directory.GetFiles(directory, pattern).Order(StringComparer.Ordinal).ToArray(); }
        catch (DirectoryNotFoundException) { return new([], 0, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new([], 0, 1); }
        for (var file = 0; file < files.Length; file++)
        {
            try
            {
                using var stream = new FileStream(files[file], FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("timestamp", out var at) ||
                            at.ValueKind != JsonValueKind.String || !at.TryGetDateTimeOffset(out var timestamp))
                        { skipped++; continue; }
                        records.Add((new(timestamp, root.Clone()), file, lineNumber));
                    }
                    catch (JsonException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unavailable++; }
        }
        return new(records.OrderBy(r => r.Record.Timestamp).ThenBy(r => r.File).ThenBy(r => r.Line)
            .Select(r => r.Record).ToArray(), skipped, unavailable);
    }

    internal static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
