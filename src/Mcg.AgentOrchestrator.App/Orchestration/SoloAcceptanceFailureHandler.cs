using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owns solo failure retention, evidence admission, retry accounting and inspectable holds.</summary>
internal sealed class SoloAcceptanceFailureHandler(
    Func<AgentOrchestratorKernel?> kernelReader,
    Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
    Func<DateTimeOffset> utcNow,
    Func<Goal, SoloAcceptanceConsumptionContext?> readContext,
    Func<GoalId, TaskId, IReadOnlyList<string>, int> recordRetryFeedback,
    Func<Goal, string, ConductorAutonomyPolicy, AttributedFailureRouteDecision, ConductorAdvanceResult> routeFailure,
    Action<Goal, GoalLifecycleState, string> recordEscalation)
{
    internal ConductorAdvanceResult Complete(Goal goal, string prefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance)
    {
        Record(goal, acceptance);
        return Hold(goal, prefix, policy, "solo-evidence-awaiting-consumption");
    }

    private void Record(Goal goal, AcceptanceVerificationSummary acceptance)
    {
        var checks = acceptance.FailedChecks ?? acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray();
        var kernel = kernelReader();
        if (kernel is not null)
        {
            kernel.RecordAcceptanceFailure(goal.Id, checks, acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation, acceptance.AttemptId, acceptance.SoloFailureReceipt);
            kernel.RouteRecordedAcceptanceFailure(goal.Id, "Solo acceptance failure retained for evidence validation.");
        }
        else
        {
            recordFailure(goal, checks, acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation);
            goal.RecordAcceptanceFailure(checks, utcNow(), acceptance.BranchHeadSha, acceptance.MainHeadSha,
                acceptance.CheckAttributions, acceptance.BaselineAttestation, acceptance.AttemptId, acceptance.SoloFailureReceipt);
        }
    }

    internal ConductorAdvanceResult Route(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        var current = readContext(goal);
        var evidence = SoloAcceptanceFailureReceiptReader.Read(goal, current);
        var route = AttributedFailureDeveloperRoute.Decide(goal, evidence.Failure, current?.CandidateSha);
        if (!route.IsRoute)
            return Hold(goal, prefix, policy, evidence.Failure is null ? evidence.Reason : route.DeclineReason!);
        if (goal.AutomaticAcceptanceRetryCount >= policy.MaxCriterionRetries)
            return Hold(goal, prefix, policy, "solo-acceptance-retry-cap");
        // The existing accounting and route marker are included in the dispatch
        // checkpoint before any worker starts. Replay never charges another retry.
        recordRetryFeedback(goal.Id, route.DeveloperTaskId!, [route.Feedback!]);
        return routeFailure(goal, prefix, policy, route);
    }

    internal static SoloAcceptanceConsumptionContext? ReadContext(Goal goal, string? executionDirectory, GateReadyCandidateRevisionPair? revisions)
    {
        var path = executionDirectory is null ? null : GoalWorktrees.TryResolve(executionDirectory, goal.Id);
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

    internal ConductorAdvanceResult Hold(Goal goal, string prefix, ConductorAutonomyPolicy policy, string reason)
    {
        var failure = goal.LatestAcceptanceFailure ?? goal.RetainedAcceptanceFailure;
        var receipt = failure?.SoloFailureReceipt;
        var next = SoloHoldNextAction.For(reason);
        var detail = $"Solo acceptance correction held: {reason}; attempt={failure?.AttemptId ?? "absent"}; " +
            $"evidence={receipt?.Evidence?.Path ?? "absent"}; sha256={receipt?.Evidence?.Sha256 ?? "absent"}" +
            $"; mechanism: {next.Mechanism}; owner: {next.Owner}; next action: {next.NextAction}";
        recordEscalation(goal, GoalLifecycleState.AcceptanceFailed, detail);
        return new(goal.Id.Value, prefix, policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, detail)
            {
                Decision = new("solo-acceptance-correction", "Hold", 1, reason, detail,
                [
                    new("goalId", goal.Id.Value),
                    new("attemptId", failure?.AttemptId ?? "absent"),
                    new("candidateSha", failure?.BranchHeadSha ?? "absent"),
                    new("mainSha", failure?.MainHeadSha ?? "absent"),
                    new("evidencePath", receipt?.Evidence?.Path ?? "absent"),
                    new("evidenceSha256", receipt?.Evidence?.Sha256 ?? "absent")
                ])
            });
    }
}
