using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliReassignAgentCommand
{
    public static bool Execute(CliExecutionContext context, TaskSpec task, IReadOnlyList<string> args, int start)
    {
        AgentDefinition? agent;
        if (args.Skip(start).Any(arg => arg.Equals("--model", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--model=", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var options = CliModelsCommand.ReadSelectionOptions(args, start, "--model", "--effort", "--profile");
                agent = InventoryModelAssignmentResolver.Resolve(options["--model"], options.GetValueOrDefault("--profile"),
                    options.GetValueOrDefault("--effort"), task.RequiredRole,
                    ModelInventoryStore.Load(context.Workspace.ModelInventoryPath), context.WorkerProfiles);
            }
            catch (InventoryAssignmentException error)
            {
                Console.Error.WriteLine($"ERROR: AGENT_REASSIGNMENT_HOLD code={error.Code} {error.Message}");
                return false;
            }
        }
        else
        {
            if (args.Count != start + 1 || args[start].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(CliCommandHelp.ReassignAgentUsage);

            var id = args[start];
            agent = new AgentCatalog(context.Agents).FindById(id);
            if (agent is null)
            {
                Console.Error.WriteLine($"ERROR: AGENT_REASSIGNMENT_HOLD code=AgentNotFound agent_id='{id}'.");
                return false;
            }
            if (agent.Role != task.RequiredRole)
            {
                Console.Error.WriteLine(
                    $"ERROR: AGENT_REASSIGNMENT_HOLD code=RoleMismatch agent_id='{agent.Id.Value}' " +
                    $"agent_role={agent.Role} required_role={task.RequiredRole}.");
                return false;
            }
            if (agent.Status != AgentStatus.Available)
            {
                Console.Error.WriteLine(
                    $"ERROR: AGENT_REASSIGNMENT_HOLD code=AgentUnavailable agent_id='{agent.Id.Value}' " +
                    $"agent_status={agent.Status}.");
                return false;
            }
        }

        var effect = task.Status == WorkTaskStatus.Running && task.LastProcess is not null ? "next-attempt"
            : task.Status == WorkTaskStatus.Running && task.LastDispatch is not null ? "rebuild-before-start"
            : "next-dispatch";
        var goal = context.CurrentGoal!;
        context.Kernel.ReassignTaskAgent(goal.Id, task.Id, agent);
        Console.WriteLine($"AGENT_REASSIGNMENT_ACKNOWLEDGED task={task.Id.Value} " +
            $"agent={agent.Id.Value} harness={agent.Subscription?.WorkerProfileName ?? "api"} effect={effect}");
        TaskTextView.PrintTask(goal, task);
        return true;
    }
}
