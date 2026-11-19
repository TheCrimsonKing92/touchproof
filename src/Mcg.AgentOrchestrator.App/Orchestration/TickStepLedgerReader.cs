using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record TickStepObservation(DateTimeOffset Timestamp, int? Tick, string Step, double Milliseconds);

internal static class TickStepLedgerReader
{
    private const string Head = "PHASE_TIMING";
    private static readonly Dictionary<string, IReadOnlyList<string>> StepGroups = new(StringComparer.Ordinal)
    {
        ["prewalk_steps"] = ConductorTickStepLedger.PrewalkNames,
        ["sweep_steps"] = ConductorTickStepLedger.SweepNames,
        ["kernel_reload_steps"] = ConductorTickStepLedger.KernelReloadNames,
        ["admission_steps"] = ConductorTickStepLedger.AdmissionNames
    };

    internal static IReadOnlyList<string> StepNames { get; } =
        Array.AsReadOnly(StepGroups.Values.SelectMany(names => names).ToArray());

    internal static IReadOnlyList<TickStepObservation> Read(string logPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        if (!Directory.Exists(directory)) return [];

        return ReadFile(logPath)
            .Concat(Directory.EnumerateFiles(directory, "conduct-events-*.log")
                .OrderBy(path => path, StringComparer.Ordinal).SelectMany(ReadFile))
            .DistinctBy(sample => (sample.Timestamp, sample.Tick, sample.Step))
            .OrderBy(sample => sample.Timestamp)
            .ToArray();
    }

    private static IEnumerable<TickStepObservation> ReadFile(string path)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { yield break; } // The active file may have rotated.
        catch (DirectoryNotFoundException) { yield break; }
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                if (ReadEvent(line) is not { } timing) continue;
                foreach (var observation in ReadSteps(timing.Timestamp, timing.Detail))
                    yield return observation;
            }
        }
    }

    private static (DateTimeOffset Timestamp, string Detail)? ReadEvent(string line)
    {
        if (!line.Contains(Head, StringComparison.Ordinal)) return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                !timestamp.TryGetDateTimeOffset(out var at) ||
                !root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.String ||
                detail.GetString() is not { } text || !text.StartsWith(Head + " ", StringComparison.Ordinal))
                return null;

            return (at, text);
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<TickStepObservation> ReadSteps(DateTimeOffset timestamp, string detail)
    {
        var tokens = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tickToken = tokens.FirstOrDefault(token => token.StartsWith("tick=", StringComparison.Ordinal));
        int? tick = tickToken is not null &&
            int.TryParse(tickToken.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;

        foreach (var token in tokens)
        {
            var separator = token.IndexOf('=');
            if (separator < 0 || !StepGroups.TryGetValue(token[..separator], out var names)) continue;
            foreach (var entry in token[(separator + 1)..].Split(','))
            {
                var fields = entry.Split(':', 3);
                if (fields.Length < 2 || !names.Contains(fields[0], StringComparer.Ordinal) ||
                    !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) ||
                    !double.IsFinite(milliseconds) || milliseconds < 0) continue;

                yield return new(timestamp, tick, fields[0], milliseconds);
            }
        }
    }
}
