using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Identity of git/approval observations, independent of admission policy and mutable Goal objects.
internal sealed record AdmissionFactsInputs(string Repository, GoalId GoalId, string Branch,
    string Main, string ExecutionHead, string FrozenPaths, string Approvals)
{
    internal string DenylistIdentity => $"{Main}:config/landing-denylist.json";
    internal string Identity => $"branch={Branch};main={Main};head={ExecutionHead};frozen={FrozenPaths};approvals={Approvals}";

    internal static string ReadApprovals(string directory, GoalId goalId)
    {
        var decisions = CollaborationItemStore.ForDirectory(
            OrchestratorWorkspace.ForDirectory(directory).OrchestratorDirectory);
        var prefix = AcceptancePolicyChangeDecision.RequestId(goalId.Value, string.Empty);
        var states = decisions.ListDecisionRequestsAsync(goalId.Value).GetAwaiter().GetResult()
            .Where(request => request.Id.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(request => request.Id, StringComparer.Ordinal)
            .Select(request => decisions.GetDecisionStateAsync(request.Id).GetAwaiter().GetResult())
            .Where(state => state?.Receipt is not null).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(states)));
    }

    internal static string PathsIdentity(IEnumerable<string> paths) => JsonSerializer.Serialize(
        paths.Select(path => path.Replace('\\', '/').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
