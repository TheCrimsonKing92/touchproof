using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public sealed record DeveloperReachCheckEntry(string Symbol, string Reference, string Disposition);

internal static class DeveloperReachCheck
{
    internal const int MaxEntries = 100;
    internal const int MaxUtf8Bytes = 32_768;

    internal static void RecordWithCriteriaSelfCheck(TaskSpec task, TaskVerificationRecord verification, Action<string> note)
    {
        var output = verification.AuthoritativeStandardOutput ?? verification.StandardOutput;
        note(DeveloperCriteriaSelfCheck.ParseWorkerOutput(output).ToTaskNote(task.Id));
        Record(task, verification, note);
    }

    internal static void Record(TaskSpec task, TaskVerificationRecord verification, Action<string> note)
    {
        if (verification.AuthoritativeStandardOutput is not { } output)
            return;

        var (entries, reason) = ParseWorkerOutput(output);
        if (reason is not null)
        {
            note($"DEVELOPER_REACH_CHECK task={task.Id.Value} field=malformed reason={reason}");
        }
        else if (entries is not null)
        {
            var dispatch = FindOriginatingDispatch(task, verification);
            if (dispatch is not null && task.TrySetDispatchReachCheck(dispatch.DispatchedAt, entries))
                note($"DEVELOPER_REACH_CHECK task={task.Id.Value} field=present entries={entries.Count}");
            else
                note($"DEVELOPER_REACH_CHECK task={task.Id.Value} field=unbound reason=Originating dispatch is unavailable.");
        }
    }

    internal static TaskDispatchRecord? FindOriginatingDispatch(TaskSpec task, TaskVerificationRecord verification)
    {
        if (verification.DispatchAttemptAt is { } attemptAt)
            return task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt == attemptAt);

        var cutoff = verification.DispatchStartedAt ?? verification.CompletedAt;
        return task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= cutoff &&
            string.Equals(dispatch.Command, verification.Command, StringComparison.Ordinal) &&
            string.Equals(dispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private static (IReadOnlyList<DeveloperReachCheckEntry>? Entries, string? Reason) ParseWorkerOutput(string output)
    {
        if (!WorkerResultMarkers.TryLocateBlock(output, out var start, out var endMarkerStart, out _))
            return (null, null);

        string? value = null;
        foreach (var raw in output[start..endMarkerStart].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = WorkerResultLineUnwrap.Unwrap(raw.Trim());
            if (!WorkerResultMarkdownEmphasis.TrySplitField(line, out var label, out var fieldValue) ||
                !WorkerResultMarkers.NormalizeWorkerResultMarker(label).Equals("reach_check", StringComparison.OrdinalIgnoreCase))
                continue;

            if (value is not null)
                return (null, "Expected at most one reach_check line.");

            value = fieldValue;
        }

        if (value is null)
            return (null, null);

        if (Encoding.UTF8.GetByteCount(value) > MaxUtf8Bytes)
            return (null, $"JSON value exceeds {MaxUtf8Bytes} UTF-8 bytes.");

        try
        {
            using var document = JsonDocument.Parse(value);
            var array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array)
                return (null, "Expected a JSON array.");

            if (array.GetArrayLength() > MaxEntries)
                return (null, $"JSON array exceeds {MaxEntries} entries.");

            var entries = new List<DeveloperReachCheckEntry>(array.GetArrayLength());
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3 ||
                    !TryGetNonBlankString(entry, "symbol", out var symbol) ||
                    !TryGetNonBlankString(entry, "reference", out var reference) ||
                    !TryGetNonBlankString(entry, "disposition", out var disposition))
                    return (null, "Each entry must have exactly symbol, reference, and disposition as non-empty strings.");

                if (disposition != "updated" &&
                    !HasDispositionDetail(disposition, "covered-by ") &&
                    !HasDispositionDetail(disposition, "unaffected - "))
                    return (null, "Invalid reference disposition.");

                entries.Add(new DeveloperReachCheckEntry(symbol, reference, disposition));
            }

            return (entries.AsReadOnly(), null);
        }
        catch (JsonException)
        {
            return (null, "Expected valid JSON on one line.");
        }
        catch (InvalidOperationException)
        {
            return (null, "Expected valid JSON string values.");
        }
    }

    private static bool TryGetNonBlankString(JsonElement entry, string property, out string value)
    {
        value = entry.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool HasDispositionDetail(string disposition, string prefix) =>
        disposition.StartsWith(prefix, StringComparison.Ordinal) && !disposition.AsSpan(prefix.Length).IsWhiteSpace();
}
