using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ForgeDiagnosticRedactor
{
    public static string Redact(string text, IReadOnlyDictionary<string, string>? environment = null)
    {
        var value = text;
        if (environment is not null && environment.TryGetValue("GH_TOKEN", out var grant) && grant.Length > 0)
        {
            value = value.Replace(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + grant)),
                "[redacted]", StringComparison.Ordinal);
            value = value.Replace(grant, "[redacted]", StringComparison.Ordinal);
        }
        value = Regex.Replace(value, @"(?i)(https?://)[^\s/@]+(?::[^\s/@]*)?@", "$1[redacted]@");
        value = Regex.Replace(value, @"(?i)\b(?:gh[pousr]_[a-z0-9_]+|github_pat_[a-z0-9_]+)\b", "[redacted]");
        value = Regex.Replace(value, @"(?i)(\b(?:authorization\s*:\s*(?:bearer|token|basic)|(?:gh|github)_token\s*[=:])\s*)\S+", "$1[redacted]");
        value = Regex.Replace(value, @"(?i)(x-access-token:)\S+", "$1[redacted]");
        value = Regex.Replace(value, @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[redacted]");
        value = Regex.Replace(value, @"(?i)([?&](?:access_token|token|password)=)[^\s&]+", "$1[redacted]");
        return value.Trim();
    }
}
