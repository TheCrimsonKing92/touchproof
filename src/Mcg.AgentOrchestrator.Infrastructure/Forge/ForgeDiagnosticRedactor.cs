using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ForgeDiagnosticRedactor
{
    public static string Redact(string text)
    {
        var value = Regex.Replace(text, @"(?i)(https?://)[^\s/@]+(?::[^\s/@]*)?@", "$1[redacted]@");
        value = Regex.Replace(value, @"(?i)\b(?:gh[pousr]_[a-z0-9_]+|github_pat_[a-z0-9_]+)\b", "[redacted]");
        value = Regex.Replace(value, @"(?i)(\b(?:authorization\s*:\s*(?:bearer|token)|(?:gh|github)_token\s*[=:])\s*)\S+", "$1[redacted]");
        value = Regex.Replace(value, @"(?i)([?&](?:access_token|token|password)=)[^\s&]+", "$1[redacted]");
        return value.Trim();
    }
}
