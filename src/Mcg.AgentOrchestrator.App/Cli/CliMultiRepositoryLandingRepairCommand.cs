using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliMultiRepositoryLandingRepairCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals(OperatorIntentVerbs.MultiRepositoryLandingRepair, StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
            if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(CliCommandHelp.MultiRepositoryLandingRepairUsage);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 2; index < args.Count; index++)
            {
                var flag = args[index];
                if (flag.ToLowerInvariant() is not ("--idempotency-key" or "--operator-actor" or "--actor-kind") ||
                    values.ContainsKey(flag) || ++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(CliCommandHelp.MultiRepositoryLandingRepairUsage);
                values.Add(flag, args[index]);
            }
            var prefix = args[1];
            var matches = !File.Exists(workspace.SqliteStatePath) ? [] :
                SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .ListGoalIdStatusesAsync().GetAwaiter().GetResult()
                    .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(goal => goal.Id).Order(StringComparer.Ordinal).ToArray();
            if (matches.Length == 0) throw new ArgumentException($"unknown-goal: {prefix}");
            if (matches.Length > 1) throw new ArgumentException($"ambiguous-goal-prefix: {string.Join(", ", matches)}");
            var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, values.GetValueOrDefault("--idempotency-key") ?? id,
                OperatorIntentVerbs.MultiRepositoryLandingRepair, matches[0], null, "{}", [],
                attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
                (time ?? TimeProvider.System).GetUtcNow(), ActorKind: attribution.ActorKind);
            var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
                .EnqueueAsync(intent).GetAwaiter().GetResult();
            OperatorIntentSubmissionOutput.PrintAnswer(persisted, workspace.OrchestratorDirectory, matches[0], json);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }
}
