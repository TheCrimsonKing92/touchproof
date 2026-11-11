using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Provenance recorded by the gate owner at admission, never reconstructed from historical proximity.
internal sealed record AcceptanceDeveloperRoundLink(string GoalId, string TaskId, DateTimeOffset DispatchedAt,
    string BaseCommit, string CandidateCommit, string ObservedMain)
{
    internal static AcceptanceDeveloperRoundLink? Capture(ConductorParallelAcceptanceCandidate candidate)
    {
        if (!Commit(candidate.BranchHeadSha) || !Commit(candidate.MainHeadSha)) return null;
        var matches = candidate.Goal.Tasks.Where(t => t.RequiredRole == AgentRole.Developer)
            .SelectMany(t => t.DispatchHistory.Select(d => (Task: t, Dispatch: d)))
            .Where(row => row.Dispatch.ResultCommit == candidate.BranchHeadSha).ToArray();
        if (matches.Length != 1) return null;
        var (task, dispatch) = matches[0];
        var receipt = dispatch.PreDispatchIntegrationReceipt;
        if (receipt is null || receipt.GoalId != candidate.Goal.Id || receipt.TaskId != task.Id ||
            receipt.ResultingCandidateSha != dispatch.BaseCommit || receipt.IntegratedMainSha != candidate.MainHeadSha ||
            !Commit(dispatch.BaseCommit) || !Commit(dispatch.ResultCommit) || !Commit(receipt.IntegratedMainSha)) return null;
        return new(candidate.Goal.Id.Value, task.Id.Value, dispatch.DispatchedAt, dispatch.BaseCommit!,
            dispatch.ResultCommit!, receipt.IntegratedMainSha);
    }

    private static bool Commit(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
