using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli
{
internal static class NextActionsTextView
{
public static void Print(
    Goal goal,
    GoalNextActions actions,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents = null,
    GoalHealthReport? health = null,
    GoalOperatorDisposition? conductorDisposition = null,
    Func<ProcessCommandLineSnapshot>? processSnapshotFactory = null,
    IClock? diagnosticsClock = null,
    string? executionDirectory = null)
{
    var processInspection = new ProcessInspectionSnapshotScope(
        processSnapshotFactory ?? ProcessCommandLines.SnapshotOperation);
    Console.WriteLine();
    Console.WriteLine($"Goal {actions.GoalId.Value[..8]} {actions.Status}: {OutputTextPreview.CreateSummary(actions.Objective).Text}");
    if (health is not null)
    {
        Console.WriteLine($"Health: {health.Disposition} score={health.Score}; recommendation: {health.Recommendation}; command: {health.SuggestedCommand}");
    }

    var verificationSatisfied = goal.Tasks.Count > 0 && goal.Tasks.All(task => task.LastVerification?.Succeeded == true);
    var operatorDisposition = conductorDisposition ?? new GoalOperatorDispositionSurface(diagnosticsClock).Evaluate(
        goal,
        pendingHumanInputCount: 0,
        verificationSatisfied,
        commandLineSnapshot: processInspection.Get(),
        acceptanceGateInFlight: HasLiveReplacementGate(executionDirectory, goal));
    PrintOperatorDisposition(operatorDisposition);

    Console.WriteLine("Next actions:");

    for (var index = 0; index < actions.Items.Count; index++)
    {
        var item = actions.Items[index];
        Console.WriteLine($"  {index + 1}. {item.Kind}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
        if (DispatchRecoveryView.EvaluateState(goal, item, processInspection.Get()) is { } dispatchState)
        {
            DispatchResultTextView.PrintDispatchState(dispatchState, "     ");
        }

        Console.WriteLine($"     command: {NextActionCommandAdvice.BuildSuggestedCommand(goal, item, agents)}");
        var control = NextActionControls.Build(goal, item, workerProfiles, agentDefinitions: agents);
        if (!string.IsNullOrWhiteSpace(control?.CostRisk))
        {
            var recommendation = string.IsNullOrWhiteSpace(control.CostRecommendation)
                ? string.Empty
                : $" {control.CostRecommendation}";
            Console.WriteLine($"     cost: {control.CostRisk}.{recommendation}");
        }
    }

    var runCommand = health?.SuggestedCommand is { Length: > 0 } hCmd
        ? hCmd
        : actions.Items.Count > 0 ? NextActionCommandAdvice.BuildSuggestedCommand(goal, actions.Items[0], agents) : null;
    if (runCommand is not null)
    {
        Console.WriteLine($"Run: {runCommand}");
    }

    Console.WriteLine();
}

internal static bool HasLiveReplacementGate(string? executionDirectory, Goal goal)
{
    if (goal.Status != GoalStatus.AcceptanceFailed || string.IsNullOrWhiteSpace(executionDirectory))
    {
        return false;
    }

    try
    {
        // Conductor outcomes are appended after Begin but carry the attempt start time.
        var latest = GoalOperationJournal.ReadLatestActiveEntryInAppendOrder(executionDirectory, goal.Id,
            entry => entry.GoalId == goal.Id &&
                (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Equals("conductor:acceptance", StringComparison.OrdinalIgnoreCase) ||
                 entry.Operation.Equals("acceptance-retry", StringComparison.OrdinalIgnoreCase)));
        return latest is { Status: GoalOperationStatus.Begin } &&
            (goal.LatestAcceptanceFailure is null || latest.At > goal.LatestAcceptanceFailure.OccurredAt);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        return false;
    }
}

public static void PrintOperatorDisposition(GoalOperatorDisposition disposition)
{
    var blockers = disposition.Blockers.Count == 0
        ? "none"
        : string.Join(",", disposition.Blockers);
    var failedGate = disposition.Blockers.Contains(AcceptanceFailedRecoveryPolicy.FailedGateBlocker);
    var reason = failedGate ? OutputTextPreview.Create(disposition.Reason) : OutputTextPreview.CreateTimeline(disposition.Reason);
    Console.WriteLine($"Disposition: state='{disposition.State}' confidence='{disposition.Confidence}' action='{disposition.NextSafeCommand}' blockers='{blockers}' reason='{reason.Text}' fresh='{disposition.FreshAt:u}'");
    if (failedGate)
    {
        foreach (var pointer in disposition.Evidence.Where(pointer => pointer.Kind is "acceptance-attempt" or "solo-failure-receipt"))
        {
            Console.WriteLine($"  {pointer.Kind} {pointer.Path} ({pointer.Detail})");
        }
    }
}

}
}

namespace Mcg.AgentOrchestrator.App.Orchestration
{
    internal static partial class GoalOperationJournal
    {
        internal static GoalOperationJournalEntry? ReadLatestActiveEntryInAppendOrder(
            string executionDirectory,
            GoalId goalId,
            Func<GoalOperationJournalEntry, bool> matches)
        {
            var path = PathFor(executionDirectory, goalId);
            // Plain entries are either a full copy of the journal or appends after compression.
            return ReadLatest(path) ?? ReadLatest(path + ".gz");

            GoalOperationJournalEntry? ReadLatest(string journalPath) =>
                File.Exists(journalPath)
                    ? ReadJournalLines(journalPath).Select(line => TryDeserialize(line, out _))
                        .LastOrDefault(entry => entry is not null && matches(entry))
                    : null;
        }
    }
}
