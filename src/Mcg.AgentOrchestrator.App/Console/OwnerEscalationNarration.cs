using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerEscalationNarration(IReadOnlyList<OwnerConductEvent> ordered,
    OwnerQuestionRoutes routes, IReadOnlyList<OwnerAttentionObservation>? attention, Func<string?, string> title)
{
    internal sealed record Narrated(string Sentence, string Why, string Next,
        string Act = "No. You can let the conductor continue.", OwnerConsoleLineSpans? Spans = null,
        OwnerActivityResolution? Resolution = null, bool Stall = false);

    internal Narrated? Narrate(int index, IReadOnlySet<string> held)
    {
        var item = ordered[index];
        var prefix = Prefix(item.GoalId);
        var name = title(item.GoalId);
        var head = Head(item);
        if (routes.Folded(item)) return null;
        if (head == "ownerless-hold-stalled") return held.Contains(prefix) ? null : new("", "", "", Stall: true);
        var question = OwnerHoldReason.FirstLine(Field(item, "question"));
        if (attention?.Any(entry => Prefix(entry.Question.GoalId) == prefix && question is not null &&
            OwnerHoldReason.FirstLine(entry.Question.Text) == question) == true) return null;
        var rawReason = Field(item, "reason");
        var authorQuestion = item.EventKind == "goal-escalation" && question is null &&
            OwnerEscalationReasonText.IsAuthorQuestion(rawReason);
        if (!authorQuestion && (head == "author-owner-question" || item.EventKind == "author"))
        {
            var routing = routes.Route(item) ?? item;
            var recipient = QuestionRecipient(Field(routing, "recipient") ?? Field(routing, "reason") ??
                Field(item, "recipient") ?? Field(item, "reason"));
            var lead = prefix + " question for " + recipient + (question is null ? " " : ": ");
            var text = question is null ? "about " + name : Words(question, "");
            return new(lead + text, text, "The conductor or operator will handle the next step.",
                Spans: question is null ? SingleTitle(name, lead.Length + "about ".Length) : null);
        }
        var subject = FailureReason(item) is { } plan ? "plan rejected: " + plan :
            question is not null ? Words(question, "") : head == "owner-review-hold" ? "approval of the completed work" :
            OwnerEscalationReasonText.Plain(rawReason, "the conductor reported a hold on " + name, Words,
                task => ordered.Take(index).LastOrDefault(value => value.EventKind == "goal-lifecycle" &&
                    Prefix(value.GoalId) == prefix && Field(value, "task")?.StartsWith(task, StringComparison.OrdinalIgnoreCase) == true)
                    is { } worker ? Role(worker) : null);
        var narrated = new Narrated(prefix + (authorQuestion ? " " : " escalated: ") + subject, subject,
            authorQuestion ? "The Author will handle the question." : "The conductor or operator will handle the next step.");
        if (authorQuestion && ordered.Skip(index + 1).FirstOrDefault(value => value.Timestamp > item.Timestamp && value.EventKind == "goal-lifecycle" &&
            Prefix(value.GoalId) == prefix && Field(value, "resolution-verb") == "answer" &&
            Field(value, "resolution-actor") == "author") is { } answer)
            narrated = narrated with { Resolution = new(answer.Timestamp, Actor: "author"),
                Sentence = narrated.Sentence.TrimEnd('.') + $"; answered by the Author at {answer.Timestamp.ToLocalTime():HH:mm:ss}.",
                Next = "The Author answered the question; work can continue." };
        return narrated;
    }

    private static string QuestionRecipient(string? token) => token?.ToLowerInvariant() switch
    {
        { } value when value.Contains("operator", StringComparison.Ordinal) => "the operator",
        { } value when value.Contains("owner", StringComparison.Ordinal) => "you",
        _ => "the Author"
    };
}
