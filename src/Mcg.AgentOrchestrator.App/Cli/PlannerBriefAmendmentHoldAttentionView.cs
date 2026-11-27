using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class PlannerBriefAmendmentHoldAttentionView
{
    internal sealed record Wait(PlannerBriefAmendmentRevisionPendingHold Hold, HumanInputRequest Request)
    {
        internal string RequestedAt => Request.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload,
        IReadOnlyList<PlannerBriefAmendmentRevisionPendingHold>? holds)
    {
        if (holds is { Count: > 0 }) payload.Add("amendmentRevisionPending", holds.Select(hold => new
        {
            state = PlannerBriefAmendmentRevisionPendingHold.State, hold,
            detail = hold.Render(), identity = hold.Identity
        }).ToArray());
        return payload;
    }

    internal static PlannerBriefAmendmentRevisionPendingHold[] Holds(AgentOrchestratorKernel kernel, IEnumerable<Goal> goals) =>
        goals.Select(goal => kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id))
            .OfType<PlannerBriefAmendmentRevisionPendingHold>().ToArray();

    internal static Wait[] Waits(AgentOrchestratorKernel kernel) =>
        Holds(kernel, kernel.Goals).Select(hold => new Wait(hold, kernel.GetHumanInputRequest(hold.RequestId))).ToArray();

    internal static IEnumerable<GoalId> CandidateGoalIds(AttentionReadProjection projection) =>
        projection.Goals.Where(goal => goal.HasWaitingTasks &&
                goal.Status is not (GoalStatus.Parked or GoalStatus.Completed or GoalStatus.Failed or
                    GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => new GoalId(goal.GoalId));

    internal static Wait[] PrintListHolds(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        bool isReadOnlyQuery, TextWriter output)
    {
        var waits = Waits(kernel);
        // The selective text route can omit goals whose only wait has already been answered.
        if (isReadOnlyQuery && File.Exists(workspace.SqliteStatePath))
        {
            var queries = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var unloadedWaits = ReadUnloadedWaitsAsync(queries, kernel.Goals).GetAwaiter().GetResult();
            waits = [.. waits, .. unloadedWaits];
        }

        Print(waits.Select(wait => wait.Hold), output);
        return waits;
    }

    private static async Task<Wait[]> ReadUnloadedWaitsAsync(IOrchestratorStateQueries queries,
        IReadOnlyCollection<Goal> loadedGoals)
    {
        var projection = await queries.ReadAttentionProjectionAsync(new AttentionProjectionReadOptions(false), CancellationToken.None);
        if (projection is null) return [];

        var ids = CandidateGoalIds(projection).Except(loadedGoals.Select(goal => goal.Id)).ToArray();
        return ids.Length == 0 ? [] : Waits(await queries.LoadGoalsAsync(ids));
    }

    internal static void PrintWaits(IReadOnlyList<Wait> waits, TextWriter output)
    {
        if (waits.Count == 0) return;

        output.WriteLine($"Operator waits: {waits.Count} {PlannerBriefAmendmentRevisionPendingHold.State} wait(s)");
        foreach (var wait in waits)
        {
            var hold = wait.Hold;
            output.WriteLine($"  [{wait.Request.Kind}] {hold.RequestId} goal={hold.GoalId} " +
                $"task={hold.TaskId} criterion={hold.CriterionIndex} owner=operator requestedAt={wait.RequestedAt}");
            output.WriteLine($"    resume: {hold.NextCommands()}");
        }
    }

    internal static bool Print(AgentOrchestratorKernel kernel, IEnumerable<Goal> goals, TextWriter? output = null) =>
        Print(Holds(kernel, goals), output ?? Console.Out);

    private static bool Print(IEnumerable<PlannerBriefAmendmentRevisionPendingHold> holds, TextWriter output)
    {
        var printed = false;
        foreach (var hold in holds)
        {
            output.WriteLine($"  [{PlannerBriefAmendmentRevisionPendingHold.State}] {hold.Render()}");
            printed = true;
        }
        return printed;
    }
}
