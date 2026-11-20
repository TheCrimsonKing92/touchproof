using System.Collections;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalDependencyCommands
{
    internal static bool ExecuteGoalDepends(CliExecutionContext context, IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, "goal-depends <goal-prefix> --on <dependency-prefix> | goal-depends <goal-prefix> --remove <dependency-prefix> | goal-depends <goal-prefix> --clear");
        var dependentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
        var onPrefix = CliCommandHandlers.GetFlagValue(parts, "--on");
        var removePrefix = CliCommandHandlers.GetFlagValue(parts, "--remove");
        var clear = CliCommandHandlers.HasCliConfirmation(parts, "--clear");
        if ((onPrefix is not null ? 1 : 0) + (removePrefix is not null ? 1 : 0) + (clear ? 1 : 0) != 1)
            throw new ArgumentException("goal-depends requires exactly one of --on, --remove, or --clear.");
        string message;
        if (clear)
        {
            context.Kernel.ClearGoalDependencies(dependentGoal.Id);
            message = $"Dependencies cleared: {dependentGoal.Id.Value[..8]}";
        }
        else
        {
            var dependencyGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, onPrefix ?? removePrefix!);
            if (onPrefix is not null)
            {
                context.Kernel.SetGoalDependency(dependentGoal.Id, dependencyGoal.Id);
                message = $"Dependency set: {dependentGoal.Id.Value[..8]} depends on {dependencyGoal.Id.Value[..8]}";
            }
            else
            {
                context.Kernel.RemoveGoalDependency(dependentGoal.Id, dependencyGoal.Id);
                message = $"Dependency removed: {dependentGoal.Id.Value[..8]} no longer depends on {dependencyGoal.Id.Value[..8]}";
            }
        }

        var write = new DependencyWrite(dependentGoal.Id, dependentGoal.DependsOn.Select(id => id.Value).ToArray(), message);
        if (parts is TrackedArguments tracked)
            tracked.Write = write;
        else
            context.RunAfterCommit(() => write.Confirm(id =>
                context.ReloadKernel().GetGoal(id).DependsOn.Select(edge => edge.Value).ToArray()));

        return true;
    }

    internal static IReadOnlyList<string> Track(IReadOnlyList<string> parts, out TrackedArguments? tracked)
    {
        tracked = parts[0].Equals("goal-depends", StringComparison.OrdinalIgnoreCase) ? new(parts) : null;
        return tracked ?? parts;
    }

    internal sealed class TrackedArguments(IReadOnlyList<string> parts) : IReadOnlyList<string>
    {
        internal DependencyWrite? Write { get; set; }

        public int Count => parts.Count;
        public string this[int index] => parts[index];
        public IEnumerator<string> GetEnumerator() => parts.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal bool Confirm(bool changed, ITransactionalOrchestratorStateRepository repository) =>
            Confirm(changed, id =>
            {
                var stored = repository.LoadGoalAsync(id).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException($"Goal {id.Value} is missing from the store.");
                return stored.DependsOn ?? [];
            });

        internal bool Confirm(bool changed, Func<GoalId, IReadOnlyList<string>> loadEdges)
        {
            if (!changed)
                return false;

            var write = Write ?? throw new InvalidOperationException(
                $"Could not verify persisted dependencies for goal {parts[1]} because no intended edge set was recorded. The write may already be committed.");
            write.Confirm(loadEdges);
            return true;
        }
    }

    internal sealed record DependencyWrite(GoalId Goal, IReadOnlyList<string> Intended, string Message)
    {
        internal void Confirm(Func<GoalId, IReadOnlyList<string>> loadEdges)
        {
            IReadOnlyList<string> stored;
            try
            {
                stored = loadEdges(Goal);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Could not verify persisted dependencies for goal {Goal.Value}: {exception.Message}. The write may already be committed.",
                    exception);
            }

            if (!Intended.ToHashSet(StringComparer.Ordinal).SetEquals(stored))
                throw new InvalidOperationException(
                    $"Persisted dependencies for goal {Goal.Value} differ from the intended set. Intended: {Format(Intended)}. Stored: {Format(stored)}.");

            Console.WriteLine($"{Message}; stored dependencies: {Format(stored.Select(id => id[..Math.Min(8, id.Length)]))}");
        }

        private static string Format(IEnumerable<string> edges)
        {
            var values = string.Join(", ", edges.Order(StringComparer.Ordinal));
            return values.Length == 0 ? "none" : values;
        }
    }

    internal static IReadOnlyList<Goal> ResolveAtCreation(CliExecutionContext context, IReadOnlyList<string> parts)
    {
        var prefixes = new List<string>();
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals("--depends-on", StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= parts.Count || string.IsNullOrWhiteSpace(parts[index + 1]) ||
                parts[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--depends-on requires a goal prefix.");
            prefixes.Add(parts[++index]);
        }

        if (prefixes.Count == 0)
            return [];
        if (parts.Any(part => part.Equals("--simple", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--run", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--depends-on is supported only by goal <objective> --depends-on <goal-prefix> and goal --brief-file <path> --depends-on <goal-prefix>; it cannot be combined with --simple, --run or --from-backlog.");

        var dependencies = new List<Goal>();
        var seen = new HashSet<GoalId>();
        foreach (var prefix in prefixes)
        {
            var dependency = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, prefix);
            if (dependency.Status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
                throw new InvalidOperationException($"GOAL_DEPENDS_ON_REJECTED dependency={dependency.Id.Value[..8]} status={dependency.Status}");
            if (seen.Add(dependency.Id))
                dependencies.Add(dependency);
        }
        return dependencies;
    }

    internal static IReadOnlyCollection<string> ApplyAtCreation(AgentOrchestratorKernel kernel, Goal created, IReadOnlyList<Goal> dependencies)
    {
        // Capture derived edges before explicit edges mutate DependsOn, including overlapping edges.
        var preparedSourceBacklogDependencies = created.DependsOn.Select(id => id.Value).ToArray();
        foreach (var dependency in dependencies)
            kernel.SetGoalDependency(created.Id, dependency.Id);
        return preparedSourceBacklogDependencies;
    }
}
