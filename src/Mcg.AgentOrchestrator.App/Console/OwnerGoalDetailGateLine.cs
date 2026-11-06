namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalDetailGateLine
{
    internal static string Text(OwnerConsoleGateProgress gate)
    {
        var members = gate.Members.Count;
        var text = $"{gate.Kind}, {members} {(members == 1 ? "member" : "members")}";
        if (!gate.Live && gate.Participation is not null) text += ", " + gate.Participation;
        else if (!gate.ScheduleKnown) text += ", schedule unknown, ETA unknown";
        else
        {
            var count = OwnerConsoleGateCount.Of(gate);
            var estimate = gate.Failing ? "failing" : gate.HasEta ? OwnerConsoleGateFormatter.Remaining(gate.RemainingSeconds) + " left" : "ETA unknown";
            text += $", {OwnerConsoleGateFormatter.CountText(gate)} lanes, {count.Failed} failed, {estimate}";
        }
        return text + OwnerConsoleGateFormatter.ShortIdentity(gate) + " (g opens the gate view)";
    }
}
