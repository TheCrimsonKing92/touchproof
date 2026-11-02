namespace Mcg.AgentOrchestrator.Core;

public static class GoalParkResolution
{
    private const string Prefix = "Goal parked:";
    private const string LegacyFallback = "Goal parked.";

    public const string Fallback = "Goal parked: reason unavailable";

    public static string Build(string reason) => $"{Prefix} {reason}";

    public static bool IsParkResolution(string? text) =>
        text?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(text, LegacyFallback, StringComparison.Ordinal);

    public static string BuildConfirmation(string goalIdPrefix) => $"Goal parked {goalIdPrefix}.";
}
