namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerActivityLinePresentation
{
    internal const string JumpKeyHint = "Home/End first/last  PgUp/PgDn page";
    internal static IReadOnlyList<string> JumpKeyHelp => [
        "Home/End: Select the first/last row in DECISIONS, BOARD or ACTIVITY.",
        "PgUp/PgDn: Move the selection one visible page up/down in DECISIONS, BOARD or ACTIVITY."];

    internal static OwnerConsoleTitleSpan TitleSpan(string title, int start, int trailingLength = 0)
    {
        var colon = title.IndexOf(": ", StringComparison.Ordinal);
        return new(start, title.Length, colon < 0 ? 0 : colon + 1, trailingLength);
    }

    internal static OwnerConsoleLineSpans SingleTitle(string title, int start) => new([TitleSpan(title, start)]);

    internal static string Line(OwnerConsoleActivityItem item) => $"{item.Timestamp.ToLocalTime():HH:mm:ss} " +
        (item.GoalPrefix.Length > 0 && !item.Phrase.Contains(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)
            ? item.GoalPrefix + " " : "") + item.Phrase;

    internal static OwnerConsoleLineSpans LineSpans(OwnerConsoleActivityItem item) =>
        (item.Spans ?? OwnerConsoleLineSpans.None).Shift(Line(item).Length - item.Phrase.Length);
}
