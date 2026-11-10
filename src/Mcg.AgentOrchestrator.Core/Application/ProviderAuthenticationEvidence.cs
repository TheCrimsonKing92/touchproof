using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>Positive authentication evidence from a failed provider invocation, never worker prose.</summary>
public static class ProviderAuthenticationEvidence
{
    private static readonly Regex Diagnostic = new(
        @"\A(?:(?:ERROR|API Error|Error|\[grok(?:-cli)?\])[: ]+)*(?:No auth credentials for cli-chat-proxy\b.*|Missing (?:authentication |auth )?credentials\b.*|Not signed in\b.*|Failed to authenticate\b.*|authentication failed\b.*|invalid (?:api key|credentials|access token|authentication credentials)\b.*|(?:credentials|access token|authentication token) (?:have |has |is )?expired\b.*|(?:API Error:?\s*|API error \(status )?401\b.*|access token could not be refreshed\b.*|refresh token was already used\b.*|refresh_token_reused\b.*|Please (?:sign|log) in(?: again)?[.!]?)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsRequired(WorkerProviderOutcome outcome)
    {
        if (outcome.ExitCode == 0) return false;
        if (TrustedLines(outcome.StandardError).Any(IsDiagnostic)) return true;
        // Plain stdout is trusted only when the whole response is a refusal, not an answer quoting one.
        var output = outcome.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return output.Length > 0 && output.All(IsDiagnostic);
    }

    private static bool IsDiagnostic(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0])) return false;
        if (Diagnostic.IsMatch(line)) return true;
        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                return error.TryGetProperty("type", out var errorType) && errorType.ValueKind == JsonValueKind.String &&
                        errorType.GetString() is "authentication_error" or "invalid_api_key" or "invalid_token" or "expired_token" ||
                    error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                        Diagnostic.IsMatch(message.GetString()!);
            return root.TryGetProperty("type", out var type) && type.GetString() is "error" or "turn.failed" &&
                root.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String &&
                Diagnostic.IsMatch(detail.GetString()!);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static IEnumerable<string> TrustedLines(string text)
    {
        var fenced = false;
        var result = false;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = line.Trim();
            if (marker.StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (marker == "WORKER_RESULT:") { result = true; continue; }
            if (marker == "END_WORKER_RESULT") { result = false; continue; }
            if (!fenced && !result) yield return line;
        }
    }
}
