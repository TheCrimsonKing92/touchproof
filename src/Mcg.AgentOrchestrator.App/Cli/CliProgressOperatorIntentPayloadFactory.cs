using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// CLI preparation requests attestation only for a human completion; application
// independently validates the persisted actor, provenance and completion pair.
internal static class CliProgressOperatorIntentPayloadFactory
{
    internal static ProgressOperatorIntentPayload Create(
        CliCommandHandlers.GoalScopedTaskMutationCommand command,
        OperatorActorKind actorKind,
        string workingDirectory)
    {
        var status = command.ProgressStatus
            ?? throw new InvalidOperationException("Prepared progress command is missing a status.");
        var text = command.Text
            ?? throw new InvalidOperationException("Prepared progress command is missing text.");
        return new ProgressOperatorIntentPayload(
            status,
            text,
            CompletionVerification: actorKind == OperatorActorKind.Human && status == WorkTaskStatus.Completed
                ? new ManualVerificationRequest(true, text, workingDirectory)
                : null);
    }
}
