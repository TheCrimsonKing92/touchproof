using Mcg.AgentOrchestrator.Core;

internal static class TypedBriefSourceFixture
{
    internal static TaskBriefSource Create(
        Goal goal, TaskSpec task, string instruction, string[]? header = null) =>
        new(goal.Id, task.Id, task.RequiredRole, task.Description,
            [
                new TaskBriefSegment(header ?? [], RoleVisibility: [task.RequiredRole]),
                new TaskBriefSegment(["## Instructions", instruction], RoleVisibility: [task.RequiredRole])
            ],
            []);
}
