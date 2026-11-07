using System.Globalization;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Uses the fold's held set so escalation and explicit stalls share suppression.
internal sealed class OwnerStallNarration
{
    internal static bool TryNarrate(OwnerConductEvent source, ISet<string> held, Func<string?, string> name,
        out OwnerAcceptanceNarration.Narrated narrated)
    {
        narrated = null!;
        var key = Prefix(source.GoalId);
        if (!held.Add(key)) return false;
        var seconds = Field(source, "repeatedForSeconds") ?? Field(source, "heldForSeconds");
        var minutes = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? Math.Max(0, (int)(duration / 60)) : 0;
        var blocker = WaitingOn(Field(source, "blocker"));
        var waitingTitle = name(source.GoalId);
        narrated = new($"Waiting: {waitingTitle} has been held {minutes} min: {blocker}",
            blocker, "Work resumes when the blocker clears.",
            "Check DECISIONS for an owner question; otherwise the conductor will retry when it can.",
            SingleTitle(waitingTitle, "Waiting: ".Length));
        return true;
    }
}
