using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

// Reads the preparation host's diagnostic, independently of the retry-context summary.
public sealed record SandboxPreparationFailure(string FirstLine, bool NativeCommandTimedOut)
{
    private const string Frame = "[dispatch-host] worker launch/run failed: System.InvalidOperationException: ";
    private const string Prefix = "Failed to apply inheritable Low integrity label to '";
    private const string Native = " Native label command: ";

    public static SandboxPreparationFailure? Read(TaskVerificationRecord? verification)
    {
        if (verification is null || verification.Succeeded || verification.WorkerResultPresent ||
            verification.ChildProcessId is > 0) return null;
        foreach (var raw in Lines(verification))
        {
            var line = raw.StartsWith(Frame, StringComparison.Ordinal) ? raw[Frame.Length..] : raw;
            if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            return new(line, ReadTimeout(line));
        }
        return null;
    }

    private static IEnumerable<string> Lines(TaskVerificationRecord verification)
    {
        var typedDispatch = verification.EvidenceSource == VerificationEvidenceSource.Dispatch &&
            verification.DispatchAttemptAt is not null;
        var text = verification.AuthoritativeStandardError ?? (typedDispatch ? "" : verification.StandardError);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) yield return line;
        if (!typedDispatch)
            foreach (var artifactLine in DispatchEvidenceSummaries.EnumerateEvidenceLines(verification,
                         includeStandardOutput: false, includeStandardError: true, includeRetainedText: false))
                yield return artifactLine;
    }

    public static SandboxPreparationFailure? ReadLatest(TaskSpec task) =>
        Read(task.LastVerification ?? task.VerificationHistory.LastOrDefault());

    public static string Suffix(TaskSpec task) => ReadLatest(task) is { } failure
        ? $" Preparation failed: {failure.FirstLine}" : string.Empty;

    private static bool ReadTimeout(string line)
    {
        var start = line.IndexOf(Native, StringComparison.Ordinal);
        if (start < 0) return false;
        // timedOut precedes output tails on the wire; tolerate a truncated tail, never a
        // similarly spelled property inside captured stdout/stderr strings.
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line[(start + Native.Length)..]));
        try
        {
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                    reader.ValueTextEquals("timedOut"))
                    return reader.Read() && reader.TokenType == JsonTokenType.True;
        }
        catch (JsonException) { }
        return false;
    }
}
