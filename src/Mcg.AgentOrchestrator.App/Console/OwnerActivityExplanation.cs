using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Explanation presentation uses only the supplied item and current decisions.
internal sealed class OwnerActivityExplanation
{
    internal static string Explain(OwnerConsoleActivityItem item, IReadOnlyList<OwnerConsoleDecision>? decisions = null)
        => ExplainWithFields(item, decisions).Text;

    internal static (string Text, OwnerConsoleFieldSnapshot Fields, OwnerConsoleStructuredText Structured) ExplainWithFields(
        OwnerConsoleActivityItem item, IReadOnlyList<OwnerConsoleDecision>? decisions = null)
    {
        // Match the exact item; another question on the same goal never makes this event actionable.
        var decision = item.OwnerQuestionId is null ? null : decisions?.FirstOrDefault(value =>
            value.Id == item.OwnerQuestionId && value.GoalPrefix.Equals(item.GoalPrefix, StringComparison.OrdinalIgnoreCase) &&
            (item.GoalId is null || value.GoalId.Equals(item.GoalId, StringComparison.OrdinalIgnoreCase)) &&
            Words(OwnerHoldReason.FirstLine(value.FullText), "") == item.Subject);
        var resolution = decision is not null ? "still waiting on you" : item.Resolution is { } resolved ?
            resolved.AutomaticRetry ? $"retried automatically at {resolved.At.ToLocalTime():HH:mm:ss}" :
                $"resolved at {resolved.At.ToLocalTime():HH:mm:ss}" + (resolved.Actor is { } actor ? " by " + ResolutionActor(actor) :
                    "; no longer listed in DECISIONS (the resolving actor was not recorded)") :
            item.OwnerQuestionId is not null ? "no longer listed in DECISIONS" : "no owner question is listed in DECISIONS for this event";
        var act = decision is not null ? $"Yes. Open DECISIONS row [{decision.Number}] {decision.GoalPrefix} {decision.Kind}: {decision.Summary}" :
            $"No. {char.ToUpperInvariant(resolution[0])}{resolution[1..]}.";
        var next = decision is not null ? "Work waits for your answer." : item.Resolution?.AutomaticRetry == true ?
            "The conductor resumed this work." : item.OwnerQuestionId is not null ?
            "This question no longer requires an owner answer." : item.Next;
        var why = item.Subject ?? item.Why;
        var nextStep = (decision is not null || item.Resolution is not null || item.OwnerQuestionId is not null ? resolution + ". " : "") + next;
        return OwnerActivityDetailPresentation.Build(item, why, nextStep, act);
    }

    private static string ResolutionActor(string actor) => actor.ToLowerInvariant() switch
    {
        "author" => "the Author",
        "conductor" => "the conductor",
        "operator" or "owner" or "owner-console" or "miles" => "the operator",
        _ => actor
    };
}
