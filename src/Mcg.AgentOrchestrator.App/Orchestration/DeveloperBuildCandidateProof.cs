using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperBuildCandidateProof
{
    internal static DeveloperBuildCandidateEvidence Read(OrchestratorWorkspace workspace, Goal goal,
        TaskSpec task, string candidate)
    {
        var dispatch = task.LastDispatch!;
        var dispatchId = BackgroundDispatchRunner.BuildDispatchId(goal.Id, task.Id, dispatch with { ProviderSessionId = null });
        var root = workspace.ResolveExecutionDirectory(goal.Id);
        var implementation = dispatch.ResultCommit;
        if (implementation is not { Length: 40 } || !implementation.All(Uri.IsHexDigit))
            return new(dispatchId, "", false);
        var tree = GitCli.Run(root, "rev-parse", "--verify", "--end-of-options", implementation + "^{tree}");
        var digest = tree.Output.Trim();
        if (!tree.Succeeded || tree.DrainTimedOut || digest.Length != 40 || !digest.All(Uri.IsHexDigit))
            return new(dispatchId, "", false);
        return new(dispatchId, "wbc1:git-tree:" + digest,
            string.Equals(implementation, candidate, StringComparison.OrdinalIgnoreCase) ||
            ConductorOnlyCandidateRange.IsConductorOnly(root, workspace.IntegrationBranch, implementation, candidate));
    }
}
