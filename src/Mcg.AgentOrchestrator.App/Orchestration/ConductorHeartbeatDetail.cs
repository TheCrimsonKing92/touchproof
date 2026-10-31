namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorHeartbeatDetail
{
    internal static string Sanitize(string value)
    {
        const int maxDetailLength = 160;
        var sanitized = value
            .Replace(' ', '_')
            .Replace('\t', '_')
            .Replace('\n', '_')
            .Replace('\r', '_')
            .Replace(',', '_')
            .Replace('|', '_')
            .Replace('[', '_')
            .Replace(']', '_');
        return sanitized.Length > maxDetailLength ? sanitized[..maxDetailLength] : sanitized;
    }
}
