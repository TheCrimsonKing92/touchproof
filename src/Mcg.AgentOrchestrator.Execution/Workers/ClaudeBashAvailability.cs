namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ClaudeBashAvailability
{
    Available = 0,
    Unusable = 1
}

internal static class ClaudeBashToolLists
{
    internal static (string Tools, string AllowedTools, string DisallowedTools) Apply(
        ClaudeBashAvailability availability,
        string tools,
        string allowedTools,
        string disallowedTools)
    {
        if (availability == ClaudeBashAvailability.Available)
            return (tools, allowedTools, disallowedTools);

        if (availability != ClaudeBashAvailability.Unusable)
            throw new ArgumentOutOfRangeException(nameof(availability), availability, null);

        // The builder's lists are comma-delimited; their permission entries contain no commas.
        // Keep retained entries verbatim, and match the Bash identifier rather than a substring.
        static string RemoveBash(string value) => string.Join(',', value.Split(',').Where(
            entry => !string.Equals(entry, "Bash", StringComparison.Ordinal)
                && !entry.StartsWith("Bash(", StringComparison.Ordinal)));

        var denied = disallowedTools.Split(',').Contains("Bash", StringComparer.Ordinal)
            ? disallowedTools
            : string.IsNullOrEmpty(disallowedTools) ? "Bash" : $"{disallowedTools},Bash";

        return (RemoveBash(tools), RemoveBash(allowedTools), denied);
    }
}
