using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class PassedCohortApprovalHoldEvaluationCache(int capacity = PassedCohortApprovalHoldEvaluationCache.Capacity)
{
    internal const int Capacity = 1024;
    internal sealed record Key(string Repository, string GoalId, string Candidate, string Main,
        string Paths, string Denylist, string Approvals);
    private sealed record DenylistKey(string Repository, string Main);
    private readonly BoundedLruFactCache<Key, LandingDenylistOwnerGate.Result> _facts = new(capacity);
    private readonly BoundedLruFactCache<DenylistKey, LandingDenylist> _denylists = new(capacity);
    internal int Count => _facts.Count;
    internal int DenylistCount => _denylists.Count;

    internal LandingDenylist LoadDenylist(string directory, string main, Func<LandingDenylist>? load = null)
    {
        var key = AcceptancePolicyChangeDecision.IsFullSha(main) ? new DenylistKey(Path.GetFullPath(directory), main) : null;
        return _denylists.GetOrCompute(key,
            load ?? (() => LandingExecutor.LoadLandingDenylist(directory, main).Denylist), _ => true, () => key);
    }

    internal LandingDenylistOwnerGate.Result Evaluate(string directory, LandingDenylist denylist,
        ICollaborationItemStore decisions, string goalId, string main, string candidate, IEnumerable<string> paths,
        Func<LandingDenylistOwnerGate.Result>? evaluate = null)
    {
        var landingPaths = paths.ToArray();
        Key? ReadKey()
        {
            if (!AcceptancePolicyChangeDecision.IsFullSha(candidate) || !AcceptancePolicyChangeDecision.IsFullSha(main)) return null;
            var prefix = AcceptancePolicyChangeDecision.RequestId(goalId, string.Empty);
            var ids = decisions.ListDecisionRequestsAsync(goalId).GetAwaiter().GetResult()
                .Where(request => request.Id.StartsWith(prefix, StringComparison.Ordinal))
                .Select(request => AcceptancePolicyChangeDecision.RequestId(goalId, request.Id[prefix.Length..]))
                .Where(id => AcceptancePolicyChangeDecision.IsFullSha(id[prefix.Length..]))
                .Append(AcceptancePolicyChangeDecision.RequestId(goalId, candidate)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var approvals = ids.Select(id => (Id: id, State: decisions.GetDecisionStateAsync(id).GetAwaiter().GetResult()))
                // Raising/reminding an unanswered request cannot change IsApprovedAsync.
                .Where(item => item.State?.Receipt is not null).Select(item =>
            {
                var (id, state) = item;
                // The gate can reuse a historical approval only while its immutable object exists.
                // Include availability so a fetched/pruned approved object cannot leave a stale denial/approval.
                var sha = id[prefix.Length..];
                var available = state?.Receipt is not null && AcceptancePolicyChangeDecision.IsFullSha(sha)
                    ? GitRevisionReader.Shared.TryResolve(directory, $"{sha}^{{commit}}", out _) : (bool?)null;
                return new
                {
                    Id = id, state?.Request.GoalId, state?.Request.Subject,
                    Actions = state?.Request.AllowedActions.Select(action => new { action.Kind, action.ActionRef }).ToArray(),
                    state?.Receipt?.ActorId, state?.Receipt?.AuthenticationAssurance,
                    ActionRef = state?.Receipt?.Response.ActionRef, Available = available
                };
            }).ToArray();
            return new(Path.GetFullPath(directory), goalId, candidate, main, Fingerprint(landingPaths),
                Fingerprint(denylist.Rules), Fingerprint(approvals));
        }
        var key = ReadKey();
        return _facts.GetOrCompute(key, () =>
            {
                var result = evaluate is not null ? evaluate() :
                    LandingDenylistOwnerGate.Evaluate(directory, denylist, decisions, goalId, main, candidate, landingPaths);
                return result with { Matches = Array.AsReadOnly(result.Matches.ToArray()) };
            },
            result => result.Fingerprint != "unavailable" && !result.HoldReason.Contains(" diagnostic=", StringComparison.Ordinal),
            ReadKey, ConductorTickStepLedger.CountApprovalHoldCache);
    }

    private static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
