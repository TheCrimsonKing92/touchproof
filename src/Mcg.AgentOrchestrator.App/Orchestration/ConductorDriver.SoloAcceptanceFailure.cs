using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal Func<Goal, SoloAcceptanceConsumptionContext?>? SoloAcceptanceContextReader { get; set; }

    private void RecordSoloAcceptanceFailure(Goal goal, AcceptanceVerificationSummary acceptance)
    {
        var checks = acceptance.FailedChecks ?? acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
        var kernel = _cohortKernel ?? _conductorTickKernel;
        if (kernel is not null)
        {
            kernel.RecordAcceptanceFailure(goal.Id, checks, acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation, acceptance.AttemptId, acceptance.SoloFailureReceipt);
            kernel.RouteRecordedAcceptanceFailure(goal.Id, "Solo acceptance failure retained for evidence validation.");
        }
        else
        {
            _recordAcceptanceFailure(goal, checks, acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation);
            goal.RecordAcceptanceFailure(checks, _utcNow(), acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation, acceptance.AttemptId, acceptance.SoloFailureReceipt);
        }
    }

    private ConductorAdvanceResult RouteSoloAcceptanceFailure(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        var current = SoloAcceptanceContextReader is { } reader ? reader(goal) : ReadSoloAcceptanceContext(goal);
        var evidence = SoloAcceptanceFailureReceiptReader.Read(goal, current);
        var route = AttributedFailureDeveloperRoute.Decide(goal, evidence.Failure, current?.CandidateSha);
        if (!route.IsRoute)
            return HoldSoloAcceptanceFailure(goal, prefix, policy, evidence.Failure is null ? evidence.Reason : route.DeclineReason!);
        if (goal.AutomaticAcceptanceRetryCount >= policy.MaxCriterionRetries)
            return HoldSoloAcceptanceFailure(goal, prefix, policy, "solo-acceptance-retry-cap");
        // The existing accounting and route marker are included in the dispatch
        // checkpoint before any worker starts. Replay never charges another retry.
        _recordCriterionRetryFeedback(goal.Id, route.DeveloperTaskId!, [route.Feedback!]);
        return RouteAttributedFailure(goal, prefix, policy, route);
    }

    private SoloAcceptanceConsumptionContext? ReadSoloAcceptanceContext(Goal goal)
    {
        var path = _executionDirectory is null ? null : GoalWorktrees.TryResolve(_executionDirectory, goal.Id);
        var revisions = ReadCurrentCohortAttributionRevisions(goal);
        if (path is null || revisions is null) return null;
        string? Read(params string[] args)
        {
            var result = GitCli.Run(path, args);
            return result.Succeeded ? result.Output.Trim() : null;
        }
        var tree = Read("rev-parse", revisions.BranchRevision + "^{tree}");
        var baseline = Read("merge-base", revisions.BranchRevision, revisions.MainRevision);
        return tree is null || baseline is null ? null : new(revisions.BranchRevision, revisions.MainRevision, tree, baseline);
    }

    private ConductorAdvanceResult HoldSoloAcceptanceFailure(Goal goal, string prefix, ConductorAutonomyPolicy policy, string reason)
    {
        var failure = goal.LatestAcceptanceFailure ?? goal.RetainedAcceptanceFailure;
        var receipt = failure?.SoloFailureReceipt;
        var detail = $"Solo acceptance correction held: {reason}; attempt={failure?.AttemptId ?? "absent"}; " +
            $"evidence={receipt?.Evidence?.Path ?? "absent"}; sha256={receipt?.Evidence?.Sha256 ?? "absent"}";
        RecordEscalation(goal, GoalLifecycleState.AcceptanceFailed, detail);
        return MakeResult(goal.Id.Value, prefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, detail));
    }
}
