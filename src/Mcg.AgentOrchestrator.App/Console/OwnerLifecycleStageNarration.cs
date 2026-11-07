using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Pure stage sentences and their shared detail phrases.
internal sealed class OwnerLifecycleStageNarration
{
    internal static bool TryNarrate(IReadOnlyList<OwnerConductEvent> ordered, int index, string name,
        string? finding, out OwnerAcceptanceNarration.Narrated narrated)
    {
        narrated = null!;
        var item = ordered[index];
        var head = Head(item);
        if (DetailPhrase(item, name, finding) is not { } stage) return false;
        narrated = new(OwnerActivityAttempts.Label(stage, DetailLead(item)!.Length + name.Length + (head == "TaskFailed" ? " back".Length : 0),
            OwnerActivityAttempts.Number(ordered, index)), FailureReason(item) is { } rejection ? "plan rejected: " + rejection : head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
            "The worker reported a blocking finding." : "The worker could not finish its task." :
            "The work reached this stage.", head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
            "The Developer will address the finding." : "The conductor will retry the worker or request help." : "Work continues through the remaining checks.",
            "No. You can let the conductor continue.", SingleTitle(name, DetailLead(item)!.Length));
        return true;
    }

    private static string? DetailLead(OwnerConductEvent item) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started on ",
        "TaskCompleted" => Role(item) + " passed ",
        "TaskFailed" => Role(item) + " sent ",
        _ => null
    };

    internal static string? DetailPhrase(OwnerConductEvent item, string title, string? finding = null) => DetailLead(item) is { } lead
        ? lead + title + (Head(item) == "TaskFailed" ? " back: " + (FailureReason(item) is { } rejected
            ? "plan rejected: " + rejected : OwnerHoldReason.FirstLine(finding) ?? (Field(item, "outcome") == "finding"
                ? "a problem needs correction" : "the worker could not finish")) : "") : null;

    internal static string? StagePhrase(OwnerConductEvent item, string? finding = null) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started",
        "TaskCompleted" => Role(item) + " passed",
        "TaskFailed" => Role(item) + " sent back: " + (FailureReason(item) is { } reason ? "plan rejected: " + reason :
            finding ?? (Field(item, "outcome") == "finding" ? "a problem needs correction" : "the worker could not finish")),
        _ => null
    };

    internal static string? FailureReason(OwnerConductEvent item)
    {
        var reason = Field(item, "rejection") ?? OwnerPlanRejectionReason.Read(item.Detail);
        if (reason is null) return null;
        var citation = Regex.Match(reason, @"target citation '([^']+)' does not exist");
        return citation.Success ? "cited a file that does not exist: " + citation.Groups[1].Value : reason;
    }
}
