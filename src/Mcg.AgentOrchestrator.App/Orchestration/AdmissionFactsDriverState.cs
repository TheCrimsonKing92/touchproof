using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Repository fact ownership and tick lifecycle, separate from the driver's admission policy.
internal sealed class AdmissionFactsDriverState
{
    internal AdmissionFactsTickReader? Reader { get; set; }
    internal bool Active { get; private set; }
    private CohortAcceptanceStore? _cohorts;
    private Func<GoalId, bool> _finalized = _ => false;

    internal void Begin(IEnumerable<Goal> goals, AgentOrchestratorKernel kernel, string directory,
        string integrationBranch, OrchestratorWorkspace? workspace, CohortAcceptanceStore? cohorts,
        Func<GoalId, bool> finalized)
    {
        if (workspace is null && Reader is null) return;
        Reader ??= new(directory, integrationBranch);
        Active = true;
        _cohorts = cohorts;
        _finalized = finalized;
        Reader.Begin(goals, goal => workspace is null ? [] : ConductorDriver.CreateAdditiveConflictMergeOptions(
            kernel, goal, workspace.ConductEventsLogPath).FrozenPaths);
    }

    internal void Stop()
    {
        Reader?.Derivation.Stop();
        Active = false;
    }

    internal IEnumerable<GoalId> PendingReceiptPartners(GoalId id)
    {
        if (!Active || _cohorts is null) yield break;
        foreach (var receipt in _cohorts.ReadPassedReceiptsForGoal(id))
            if (receipt.HasAuthoritativeLandingEvidence && receipt.Identity.Members.All(member =>
                    Reader!.Inputs(member.GoalId) is { } input &&
                    input.Main == receipt.Identity.ObservedMainRevision && input.Branch == member.CandidateRevision &&
                    !_finalized(member.GoalId)))
                foreach (var member in receipt.Identity.Members) yield return member.GoalId;
    }

    internal GateReadyCandidateProjectionResult ApplyApproval(Goal goal, GateReadyCandidateProjectionResult projection,
        string? directory) => Active ? Reader!.ApplyApproval(goal.Id, projection) :
        ConductorTickStepLedger.Measure("projection-owner-approval", () =>
            GroupedAdmissionOwnerApprovalHold.Apply(projection, goal, directory));

    internal AdditiveConflictProbeResult Probe(GoalId id, Func<AdditiveConflictProbeResult> live)
    {
        if (!Active) return live();
        var probe = Reader!.Facts(id)?.ConflictProbe;
        if (probe is not null) ConductorTickStepLedger.CountMergeConflictCache(hit: true);
        return probe ?? new(null, null, AdditiveConflictProbeOutcome.Indeterminate, "admission-facts-pending");
    }

    internal GateReadyCandidateProjectionResult Project(Goal goal, ConductorAutonomyPolicy policy,
        GateReadyProjectionCache cache, Func<Goal, GoalLifecycleFacts> getFacts, Func<Goal, bool> gateSatisfied,
        Func<GateReadyCandidateProjectionResult> live,
        Func<Goal, GateReadyProjectionCache.Heads?>? readHeads, OrchestratorWorkspace? workspace,
        string directory, string integrationBranch)
    {
        if (Active) return Reader!.Project(goal, policy, getFacts, gateSatisfied);
        return ConductorTickStepLedger.Measure("projection-cache", () => cache.GetOrProject(goal.Id, ReadKey(), live, ReadKey));

        GateReadyProjectionCache.Key? ReadKey() => ConductorTickStepLedger.Measure("projection-key", () =>
        {
            try
            {
                var heads = ConductorTickStepLedger.Measure("projection-key-refs", () =>
                    readHeads is not null ? readHeads(goal) : ReadHeads(goal));
                return heads is null ? null : ConductorTickStepLedger.Measure("projection-key-snapshot", () =>
                    GateReadyProjectionCache.CreateKey(goal, policy, heads, getFacts(goal)));
            }
            catch { return null; } // The original projection owns failures outside a repository admission tick.
        });

        GateReadyProjectionCache.Heads? ReadHeads(Goal candidate)
        {
            if (workspace is null) return null;
            var worktree = GoalWorktrees.TryResolve(directory, candidate.Id);
            if (worktree is null) return null;
            var pair = ConductorGitRevisionReader.ReadRequiredPair(worktree, integrationBranch);
            return new(pair.BranchRevision!, pair.MainRevision!, ConductorGitRevisionReader.ReadRequiredCommit(directory, "HEAD"));
        }
    }
}
