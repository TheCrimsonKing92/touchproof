namespace Mcg.AgentOrchestrator.Infrastructure;

public static class BacklogPriorityLevels
{
    public const string Critical = "critical";
    public const string High = "high";
    public const string Normal = "normal";
    public const string Low = "low";
    public const string ValidLevelsText = Critical + ", " + High + ", " + Normal + ", " + Low;
    public const string UsageLevels = Critical + "|" + High + "|" + Normal + "|" + Low;

    public static bool TryParse(string? text, out string level)
    {
        level = text?.Trim().ToLowerInvariant() switch
        {
            Critical => Critical,
            High => High,
            Normal => Normal,
            Low => Low,
            _ => ""
        };
        return level.Length > 0;
    }

    public static string ParseOrThrow(string text) => TryParse(text, out var level)
        ? level
        : throw new ArgumentException($"--priority must be one of: {ValidLevelsText}.");

    public static string Read(string? text) => TryParse(text, out var level) ? level : Normal;

    public static int Rank(string? text) => Read(text) switch
    {
        Critical => 0,
        High => 1,
        Low => 3,
        _ => 2
    };
}
