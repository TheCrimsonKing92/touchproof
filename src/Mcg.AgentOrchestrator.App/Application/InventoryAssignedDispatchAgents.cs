using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>Validates explicit assignments and supplies agents only to dispatch preparation.</summary>
internal static class InventoryAssignedDispatchAgents
{
    public static ModelInventory? LoadForGoal(OrchestratorWorkspace workspace, Goal goal) =>
        goal.Tasks.Any(task => InventoryModelAssignment.IsInventoryId(task.AssignedAgentId))
            ? ModelInventoryStore.Load(workspace.ModelInventoryPath) : null;

    public static AgentDefinition? Resolve(OrchestratorWorkspace workspace, TaskSpec task, WorkerProfileCatalog profiles)
    {
        var assignment = InventoryModelAssignment.Parse(task.AssignedAgentId);
        if (assignment is null) return null;
        return Resolve(task, assignment, ModelInventoryStore.Load(workspace.ModelInventoryPath), profiles);
    }

    public static bool MatchesPreparedBinding(TaskDispatchRecord? dispatch, InventoryModelAssignment assignment) =>
        dispatch is not null &&
        string.Equals(dispatch.AssignedAgentId, assignment.ToAgentId().Value, StringComparison.Ordinal) &&
        string.Equals(dispatch.WorkerName, assignment.Profile, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(dispatch.ProviderName, assignment.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(dispatch.ModelName, assignment.Model, StringComparison.Ordinal) &&
        string.Equals(dispatch.ReasoningEffort, assignment.Effort, StringComparison.Ordinal);

    public static DispatchAssignmentHold? ExecutionFailure(TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Failed || task.LastDispatch?.AssignedAgentId != task.AssignedAgentId?.Value ||
            InventoryModelAssignment.Parse(task.AssignedAgentId) is not { } assignment)
            return null;
        return new(DispatchAssignmentHoldCode.InventoryExecutionFailed,
            $"Inventory assignment '{assignment.Provider}/{assignment.Model}' on '{assignment.Profile}' failed. Retry it or use reassign-agent.",
            task.AssignedAgentId?.Value, assignment.Profile);
    }

    public static ConductorAdvanceOutcome.Held? FailedGoalHold(Goal goal, GoalLifecycleState state)
    {
        var hold = goal.Tasks.Select(ExecutionFailure).OfType<DispatchAssignmentHold>().FirstOrDefault();
        return hold is null ? null : new(state, hold.Message)
        {
            Owner = ConductorHoldOwner.Operator,
            Decision = new("dispatch-assignment", "hold", 0, hold.Code.ToString(), hold.Message,
                [new("assigned-agent", hold.AssignedAgentId!)])
        };
    }

    public static InventoryDispatchBatch ForBatch(
        OrchestratorWorkspace workspace, Goal goal, IReadOnlyList<AgentDefinition> roster,
        WorkerProfileCatalog profiles, IReadOnlySet<TaskId> excluded)
    {
        ModelInventory? inventory = null;
        var agents = new Dictionary<AgentId, AgentDefinition>();
        var blocked = new Dictionary<TaskId, ReadyBlockedDiagnostic>();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned && !excluded.Contains(task.Id)))
        {
            var assignment = InventoryModelAssignment.Parse(task.AssignedAgentId);
            if (assignment is null) continue;
            try
            {
                var agent = Resolve(task, assignment, inventory ??= ModelInventoryStore.Load(workspace.ModelInventoryPath), profiles);
                agents.TryAdd(agent.Id, agent);
            }
            catch (DispatchAssignmentHoldException error)
            {
                blocked.Add(task.Id, DispatchReadinessRules.BuildReadyBlockedDiagnostic(
                    goal, task, roster, error.Hold.Code.ToString(), [error.Hold.Message]));
            }
        }
        return new(agents.Count == 0 ? roster : [.. roster, .. agents.Values], blocked);
    }

    private static AgentDefinition Resolve(
        TaskSpec task, InventoryModelAssignment assignment, ModelInventory inventory, WorkerProfileCatalog profiles)
    {
        if (!IsEligible(task, assignment, inventory, profiles))
            throw new DispatchAssignmentHoldException(new(DispatchAssignmentHoldCode.InventoryAssignmentIneligible,
                IneligibleMessage(assignment), task.AssignedAgentId?.Value, assignment.Profile));
        return assignment.ToDispatchAgent();
    }

    public static bool IsEligible(
        TaskSpec task, InventoryModelAssignment assignment, ModelInventory inventory, WorkerProfileCatalog profiles)
    {
        var entry = inventory.Find(assignment.Provider, assignment.Model)
            .FirstOrDefault(entry => ModelInventory.Same(entry.Profile, assignment.Profile));
        return task.RequiredRole == assignment.Role && entry is not null &&
            InventoryModelAssignmentResolver.RunnableProfile(entry, profiles) is { } worker &&
            InventoryModelAssignmentResolver.SupportsEffort(assignment.Provider, worker, assignment.Effort);
    }

    public static string IneligibleMessage(InventoryModelAssignment assignment) =>
        $"Inventory assignment '{assignment.Provider}/{assignment.Model}' on '{assignment.Profile}' is ineligible. Refresh the inventory or use reassign-agent.";
}

internal sealed record InventoryDispatchBatch(
    IReadOnlyList<AgentDefinition> Agents, IReadOnlyDictionary<TaskId, ReadyBlockedDiagnostic> Blocked);
