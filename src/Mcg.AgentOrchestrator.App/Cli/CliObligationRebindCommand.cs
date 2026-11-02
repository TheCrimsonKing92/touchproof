using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliObligationRebindCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals(OperatorIntentVerbs.ObligationRebind, StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        try
        {
            CliCommandHelp.ThrowIfInvalidFlags(args);
            args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
            if (args.Count < 4 || args[1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(CliCommandHelp.ObligationRebindUsage);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 4; index < args.Count; index++)
            {
                var flag = args[index];
                if (flag.ToLowerInvariant() is not ("--text-file" or "--idempotency-key" or "--operator-actor" or "--actor-kind"))
                    throw new ArgumentException(CliCommandHelp.ObligationRebindUsage);
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{flag} requires a value. {CliCommandHelp.ObligationRebindUsage}");
                values[flag] = args[index];
            }
            ObligationRebindIntentHandler.ValidatePair(args[2], args[3]);
            var path = values.GetValueOrDefault("--text-file");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ArgumentException("obligation-rebind requires an existing --text-file.");
            var reason = File.ReadAllText(path).Trim();
            if (reason.Length == 0) throw new ArgumentException("obligation-rebind requires a nonempty --text-file.");
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
                OperatorIntentVerbs.ObligationRebind, matches[0], null,
                JsonSerializer.Serialize(new ObligationRebindOperatorIntentPayload(args[2], args[3], reason), OperatorIntentJson.Options), [],
                attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, time.GetUtcNow(),
                ActorKind: attribution.ActorKind);
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
