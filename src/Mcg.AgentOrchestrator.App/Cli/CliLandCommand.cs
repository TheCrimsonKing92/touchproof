using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliLandCommand
{
    internal static bool Execute(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        CliArgumentParser.RequirePartCount(parts, 2, "land <goal-id-prefix>");
        context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
        var landEngineHealth = PostLandingCanaryFactory.CreateCircuit(context.Workspace).Read();
        var landEngineDecision = AcceptanceEngineAcceptanceGate.Decide(
            landEngineHealth.Health,
            AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
        if (!landEngineDecision.Allowed)
        {
            Console.WriteLine(
                $"Land blocked: {landEngineDecision.Reason}. " +
                "Repair it, then run acceptance-engine clear <note>.");
            return false;
        }

        var landResult = LandingExecutor.Execute(
            context.Kernel,
            context.CurrentGoal,
            context.Workspace,
            context.Channel,
            eventWriter: context.EventWriter,
            mutationBlocker: () => PostLandingCanaryFactory.BuildMutationBlockReason(context.Workspace),
            pullRequestPublisher: context.PullRequestPublisher);
        Console.WriteLine($"Land {landResult.GoalPrefix}: {landResult.Message}");
        Console.WriteLine($"  decision: {(landResult.Decision is LandingDecision.Promote ? "Promote" : $"Escalate({((LandingDecision.Escalate)landResult.Decision).Reason})")}");
        Console.WriteLine($"  integration-branch: {landResult.IntegrationBranch}");
        Console.WriteLine($"  main-advanced: {landResult.MainAdvanced}");
        if (landResult.MainAdvanced)
        {
            var landChangedFiles = landResult.ChangedFiles
                ?? throw new InvalidOperationException(
                    "Landing advanced main without an authoritative changed-file receipt.");
            PostLandingCanaryFactory.HandleLandingAfterMainAdvanced(
                context.Workspace,
                new ConductorLandingReceipt(
                    context.CurrentGoal.Id.Value,
                    landChangedFiles,
                    landResult.MergeCommitSha),
                Console.WriteLine);
        }
        return landResult.MainAdvanced || landResult.StateChanged;
    }
}
