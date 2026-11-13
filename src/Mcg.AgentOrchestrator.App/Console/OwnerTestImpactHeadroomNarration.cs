using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerTestImpactHeadroomNarration
{
    internal static OwnerConsoleActivityItem? NarrateItem(IReadOnlyList<OwnerConductEvent> ordered, int index,
        Func<string?, string> name) => TryNarrate(ordered[index], out var sentence, out var why, out var next, out var act)
            ? OwnerActivityNarrator.Item(ordered[index], name(ordered[index].GoalId), ordered, sentence, why, next, act) : null;

    internal static bool TryNarrate(OwnerConductEvent evt, out string sentence, out string reason,
        out string nextStep, out string ownerAction)
    {
        sentence = reason = nextStep = ownerAction = "";
        var bound = OwnerEventFields.Field(evt, "bound");
        if (string.IsNullOrWhiteSpace(bound) ||
            !int.TryParse(OwnerEventFields.Field(evt, "count"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var count) ||
            !int.TryParse(OwnerEventFields.Field(evt, "cap"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var cap)) return false;

        sentence = $"Focused test selection near its limit: {bound} {count.ToString(CultureInfo.InvariantCulture)} of {cap.ToString(CultureInfo.InvariantCulture)}";
        reason = "Above the limit, focused selection falls back to the full Infrastructure suite.";
        nextStep = "Selection keeps working until a candidate passes the limit.";
        ownerAction = "Yes. Raise the limit or shrink the test closure before a candidate passes it.";
        return true;
    }
}
