using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The tick observes refs from files, never through GitRevisionReader's process fallback.
internal sealed class AdmissionFactsTickReader(string directory, string integrationBranch,
    Func<IReadOnlyList<AdmissionFactsInputs>, CancellationToken, IReadOnlyList<AdmissionFacts>>? derive = null,
    Func<DateTimeOffset>? utcNow = null)
{
    private readonly AdmissionFactsRepository _repository = new(integrationBranch);
    private AdmissionFactsDerivation? _derivation;
    internal AdmissionFactsDerivation Derivation => _derivation ??= new(derive ?? _repository.Derive,
        utcNow, () => _repository.LastMergeLaunches);
    private readonly Dictionary<GoalId, (AdmissionFactsInputs? Inputs, AdmissionFacts? Facts)> _tick = [];
    private readonly Dictionary<GoalId, DateTimeOffset> _unresolvedSince = [];

    internal void Begin(IEnumerable<Goal> goals, Func<Goal, IReadOnlyCollection<string>> frozenPaths)
    {
        _tick.Clear();
        var candidates = goals.ToArray();
        Derivation.Retain(candidates.Select(goal => goal.Id).ToHashSet());
        foreach (var id in _unresolvedSince.Keys.Except(candidates.Select(goal => goal.Id)).ToArray()) _unresolvedSince.Remove(id);
        foreach (var goal in candidates)
        {
            var inputs = ConductorTickStepLedger.Measure("projection-inputs", () => ReadInputs(goal.Id,
                ConductorTickStepLedger.Measure("projection-inputs-frozen", () => frozenPaths(goal))));
            var facts = ConductorTickStepLedger.Measure("projection-facts-lookup", () => inputs is null ? null : Derivation.Read(inputs));
            var age = inputs is null ? TimeSpan.Zero : Derivation.Request(inputs);
            if (inputs is null)
            {
                var now = utcNow?.Invoke() ?? DateTimeOffset.UtcNow;
                if (!_unresolvedSince.TryGetValue(goal.Id, out var since)) _unresolvedSince[goal.Id] = since = now;
                age = now - since;
            }
            else _unresolvedSince.Remove(goal.Id);
            ConductorTickStepLedger.CountApprovalHoldCache(facts is not null);
            _tick[goal.Id] = (inputs, facts);
            ConductorTickStepLedger.CountAdmissionFacts(goal.Id, facts is not null, age);
        }
        Derivation.Start();
    }

    internal AdmissionFacts? Facts(GoalId goalId) => _tick.GetValueOrDefault(goalId).Facts;
    internal AdmissionFactsInputs? Inputs(GoalId goalId) => _tick.GetValueOrDefault(goalId).Inputs;

    internal GateReadyCandidateProjectionResult Project(Goal goal, ConductorAutonomyPolicy policy,
        Func<Goal, GoalLifecycleFacts> getFacts, Func<Goal, bool> gateSatisfied)
    {
        var (inputs, facts) = _tick.GetValueOrDefault(goal.Id);
        ConductorTickStepLedger.CountProjectionCache(facts is not null);
        if (facts is null) return new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.AdmissionFactsPending)
        { HoldReason = inputs?.Identity ?? "unresolved" };
        return ConductorTickStepLedger.Measure("projection-policy", () => GateReadyProjectionCache.ProjectUncached(
            goal, policy, getFacts, gateSatisfied, _ => facts.Risk, facts.Projector()));
    }

    internal GateReadyCandidateProjectionResult ApplyApproval(GoalId id, GateReadyCandidateProjectionResult projection) =>
        ConductorTickStepLedger.Measure("approval-facts-lookup", () =>
        {
            var facts = Facts(id);
            return facts?.ApplyApproval(projection) ?? projection;
        });

    internal IEnumerable<string> DrainReports() => Derivation.DrainReports().Concat(_repository.DrainReports());

    private AdmissionFactsInputs? ReadInputs(GoalId id, IReadOnlyCollection<string> frozenPaths)
    {
        var revisions = ConductorTickStepLedger.Measure<(string Branch, string Main, string Head)?>("projection-inputs-refs", () =>
        {
            if (!AdmissionFactsRefReader.TryResolve(directory, $"refs/heads/{GoalWorktrees.BranchName(id)}", out var branch) ||
                !AdmissionFactsRefReader.TryResolve(directory, $"refs/heads/{integrationBranch}", out var main) ||
                !AdmissionFactsRefReader.TryResolve(directory, "HEAD", out var executionHead)) return null;
            return (branch, main, executionHead);
        });
        if (revisions is not { } heads) return null;
        var approvals = ConductorTickStepLedger.Measure("projection-inputs-approvals", () =>
            AdmissionFactsInputs.ReadApprovals(directory, id));
        return ConductorTickStepLedger.Measure("projection-inputs-key", () =>
            new AdmissionFactsInputs(Path.GetFullPath(directory), id, heads.Branch, heads.Main, heads.Head,
                AdmissionFactsInputs.PathsIdentity(frozenPaths), approvals));
    }
}
