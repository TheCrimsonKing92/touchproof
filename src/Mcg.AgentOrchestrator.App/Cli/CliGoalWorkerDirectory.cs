using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalWorkerDirectory
{
    public static string Resolve(CliExecutionContext context)
    {
        try
        {
            return GoalWorkerDirectory.Resolve(context.Workspace, context.CurrentGoal!.Id);
        }
        catch (MissingGoalWorktreeException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            throw new CliExitException(1);
        }
    }
}
