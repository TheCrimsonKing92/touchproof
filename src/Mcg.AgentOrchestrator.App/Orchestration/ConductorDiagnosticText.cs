namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Bounds diagnostic text for the conductor's compact record formats.</summary>
internal static class ConductorDiagnosticText
{
    internal static string Sanitize(string value)
    {
        var s = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return s.Length > 40 ? s[..40] : s;
    }

    internal static string SanitizeReason(string value)
    {
        const int maxReasonLength = 512;
        var sanitized = value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');
        return sanitized.Length > maxReasonLength ? sanitized[..maxReasonLength] : sanitized;
    }

    internal static string BoundSingleLine(string value)
    {
        const int maxLength = 512;
        var singleLine = value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length > maxLength ? singleLine[..maxLength] : singleLine;
    }
}
