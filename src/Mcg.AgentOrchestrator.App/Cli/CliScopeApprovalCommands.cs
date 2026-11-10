using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Cli.CliPersistentStateRunner;

namespace Mcg.AgentOrchestrator.App.Cli;

// Routes bounded scope commands; each handler retains its own authority and lifetime contract.
internal static class CliScopeApprovalCommands
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        (args[0].Equals(OperatorIntentVerbs.IntegrationScope, StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals(OperatorIntentVerbs.ApprovePolicyChange, StringComparison.OrdinalIgnoreCase));

    internal static IntegrationScopeAuthorization ParseIntegrationScope(IReadOnlyList<string> args, string goalId)
    {
        CliCommandHelp.ThrowIfInvalidFlags(args);
        if (args.Count < 6 || !Guid.TryParseExact(args[2], "N", out _) ||
            !ConductorConflictIntegration.FullSha(args[3]) || !ConductorConflictIntegration.FullSha(args[4]))
            throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage);
        var action = args[5] switch
        {
            "approve" => IntegrationScopeAction.Approve,
            "original-only" => IntegrationScopeAction.OriginalOnly,
            "revoke" => IntegrationScopeAction.Revoke,
            _ => throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage)
        };
        var paths = new List<string>();
        for (var index = 6; index < args.Count; index++)
            if (args[index] == "--path")
            {
                if (++index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage);
                paths.Add(args[index]);
            }
        if (string.IsNullOrWhiteSpace(ResolveFlagValue(args, "--idempotency-key")))
            throw new ArgumentException("Integration scope requires an explicit idempotency key.");
        var reasonPath = ResolveFlagValue(args, "--text-file") ?? throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage);
        var reason = File.ReadAllText(reasonPath);
        var approval = ResolveFlagValue(args, "--approval");
        if (string.IsNullOrWhiteSpace(reason) ||
            (action == IntegrationScopeAction.Approve ? paths.Count == 0 || approval is not null : paths.Count != 0) ||
            (action == IntegrationScopeAction.Revoke ? string.IsNullOrWhiteSpace(approval) : approval is not null))
            throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage);
        return new(goalId, args[2], args[3].ToLowerInvariant(), args[4].ToLowerInvariant(), action, paths, reason, approval);
    }

    internal static bool Submit(IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository, OrchestratorWorkspace workspace,
        ref Goal? currentGoal, OperatorIntentSubmissionSource submissionSource)
    {
        if (args[0].Equals(OperatorIntentVerbs.ApprovePolicyChange, StringComparison.OrdinalIgnoreCase))
            return SubmitPolicyChangeApprovalIntent(args, stateRepository, workspace, ref currentGoal, submissionSource);
        args = OperatorIntentSubmissionOutput.StripJsonFlag(args, out var json);
        if (args.Count < 2) throw new ArgumentException(CliCommandHelp.IntegrationScopeUsage);
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, args[1]);
        var payload = ParseIntegrationScope(args, goalId.Value);
        var attribution = ResolveOperatorIntentAttribution(args, submissionSource);
        if (attribution.ActorKind != OperatorActorKind.Human || attribution.AuthenticationAssurance != "local-process")
            throw new InvalidOperationException("Integration scope requires a human local operator.");
        var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), ResolveFlagValue(args, "--idempotency-key")!,
            OperatorIntentVerbs.IntegrationScope, goalId.Value, null, JsonSerializer.Serialize(payload, OperatorIntentJson.Options),
            [Path.GetFullPath(ResolveFlagValue(args, "--text-file")!)], attribution.Actor, attribution.Channel,
            attribution.AuthenticationAssurance, DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent).GetAwaiter().GetResult();
        OperatorIntentSubmissionOutput.PrintAnswer(persisted, workspace.OrchestratorDirectory, goalId.Value, json);
        return false;
    }
}
