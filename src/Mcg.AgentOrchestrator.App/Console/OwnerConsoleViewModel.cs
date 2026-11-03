using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleSelectedGoalPreview(string? GoalId, OwnerGoalChangeSummary? Summary,
    string? Unavailable = null);

internal sealed record OwnerConsoleViewModel(
    OwnerConsoleStatus Status, ImmutableArray<OwnerConsoleDecision> Decisions,
    ImmutableArray<OwnerConsoleBoardRow> Board, ImmutableArray<OwnerConsoleActivityItem> Activity,
    OwnerConsolePaneState? DecisionsState = null, OwnerConsolePaneState? ActivityState = null,
    OwnerConsoleGateProgress? Gate = null, OwnerConsoleBoardScope? BoardScope = null);

internal sealed record OwnerConsoleStatus(bool ConductorRunning, int ActiveGoals, int LiveDecisions,
    int HiddenQuestions, TimeSpan? LastEventAge, int LandedToday, int FailedToday = 0, DateTimeOffset? LatestTickAt = null,
    string? PlannedStopReason = null, DateTimeOffset? PlannedStopAt = null);

internal sealed record OwnerConsoleDecision(string Id, int Number, string GoalId, string GoalPrefix,
    OwnerQuestionKind Kind, string Summary, string FullText, string? BlastRadius,
    string? Confidence, string? ProposedDefault, DateTimeOffset? RaisedAt = null, bool CanAnswer = true)
{
    internal OwnerQuestion ToQuestion() => new(Id, GoalId, Kind, FullText, BlastRadius, Confidence, ProposedDefault, CanAnswer);
    internal OwnerConsoleFieldSnapshot Fields() => OwnerConsoleFieldSnapshot.Create(Id,
        new("id", "Decision ID", Id), new("goal", "Goal ID", GoalId), new("kind", "Kind", Kind.ToString()),
        new("number", "Row number", Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("summary", "Summary", Summary), new("question", "Question", FullText),
        new("blast", "Blast radius", BlastRadius), new("confidence", "Confidence", Confidence),
        new("default", "Proposed default", ProposedDefault), new("raised", "Raised at", RaisedAt?.ToString("O")));
}

internal sealed record OwnerConsoleBoardRow(string GoalPrefix, string Epic, string Title,
    string State, string Stage, TimeSpan? Age, string GoalId, bool Dimmed = false, string Priority = "",
    string? PriorityWarning = null)
{
    internal string Project { get; init; } = "";
    internal bool IsForeign { get; init; }
    internal bool IsDiagnostic { get; init; }
    internal OwnerConsoleBoardSortKey SortKey { get; init; }
    internal string? StageWithoutQuestion { get; init; }
    internal int OwnerDecisions { get; init; }
    internal bool Collapsed { get; init; }
    internal bool IsPassedGateHeld { get; init; }
    internal OwnerConsoleFieldSnapshot Fields() => OwnerConsoleFieldSnapshot.Create(Project + "/" + GoalId,
        new("goal", "Goal ID", GoalId), new("epic", "Epic", Epic), new("title", "Title", Title),
        new("priority", "Priority", Priority), new("stage", "Stage", Stage),
        new("age", "Age", Age?.ToString("c")), new("state", "State", State), new("project", "Project", Project),
        new("warning", "Priority warning", PriorityWarning));
}

internal sealed record OwnerConsoleActivityItem(DateTimeOffset Timestamp, string Kind, string Tag,
    string GoalPrefix, string Detail, string GoalTitle = "", string Phrase = "",
    string Why = "The reason was not recorded.", string Next = "Work continues.", string Act = "No action is needed.",
    string? Subject = null, string? OwnerQuestionId = null, OwnerActivityResolution? Resolution = null,
    OwnerConsoleLineSpans? Spans = null, string? GoalId = null, string? Question = null,
    IReadOnlyList<string>? GoalPrefixes = null)
{
    internal OwnerConsoleFieldSnapshot Fields() => OwnerConsoleFieldSnapshot.Create($"{Timestamp:O}/{Kind}/{GoalId ?? GoalPrefix}/{Detail}",
        new("goal", "Goal ID", GoalId), new("timestamp", "Timestamp", Timestamp.ToString("O")),
        new("kind", "Kind", Kind), new("tag", "Tag", Tag), new("title", "Goal title", GoalTitle),
        new("detail", "Detail", Detail), new("phrase", "Phrase", Phrase), new("why", "Why", Why),
        new("next", "Next", Next), new("act", "Action", Act), new("subject", "Subject", Subject),
        new("question-id", "Question ID", OwnerQuestionId), new("question", "Question", Question),
        new("references", "Observed goal references", GoalPrefixes is null ? null : string.Join("\n", GoalPrefixes)),
        new("resolution/at", "Resolved at", Resolution?.At.ToString("O")), new("resolution/actor", "Resolution actor", Resolution?.Actor));
}

internal sealed record OwnerConsoleViewInputs(DateTimeOffset OpenedAt, DateTimeOffset? LastConductEvent,
    IReadOnlyList<OwnerConductEvent> RecentEvents, int LandedToday);
