using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerAttentionNarration(IReadOnlyList<OwnerConductEvent> ordered, Func<string?, string> fullName)
{
    internal IReadOnlyList<OwnerConsoleActivityItem> Narrate(IReadOnlyList<OwnerAttentionObservation>? attention)
    {
        var result = new List<OwnerConsoleActivityItem>();
        foreach (var entry in attention ?? [])
        {
            var question = Words(OwnerHoldReason.FirstLine(entry.Question.Text), "the recorded owner question");
            var source = new OwnerConductEvent(entry.FirstSeen, "owner-question", entry.Question.GoalId, "",
                OwnerActivityDetailEvidence.From(entry.Question.Text));
            Add(source, "Needs you: " + Prefix(entry.Question.GoalId) + " " + question,
                question, "Work waits for your answer.");
            var resolution = OwnerActivityProgress.Resolution(entry, ordered);
            result[^1] = result[^1] with { Tag = "decision", Subject = question, OwnerQuestionId = entry.Question.ItemId,
                Resolution = resolution, Question = entry.Question.Text };
            if (entry.ResolvedAt is { } resolvedAt)
            {
                var resolvedTitle = fullName(entry.Question.GoalId);
                var hasTitle = resolvedTitle != Prefix(entry.Question.GoalId);
                Add(source with { Timestamp = resolvedAt, EventKind = "owner-question-resolved" },
                    "Resolved: " + Prefix(entry.Question.GoalId) +
                    (hasTitle ? " " + resolvedTitle + ": " : " ") + question,
                    question, "The conductor can continue.", spans: hasTitle
                        ? SingleTitle(resolvedTitle, "Resolved: ".Length + Prefix(entry.Question.GoalId).Length + 1) : null);
                result[^1] = result[^1] with { Subject = question, OwnerQuestionId = entry.Question.ItemId,
                    Resolution = resolution, GoalId = entry.Question.GoalId, Question = entry.Question.Text };
            }
        }
        return result;

        void Add(OwnerConductEvent source, string sentence, string why, string next, OwnerConsoleLineSpans? spans = null) =>
            result.Add(Item(source, fullName(source.GoalId), ordered, sentence, why, next, spans: spans));
    }
}
