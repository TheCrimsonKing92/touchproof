using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Application;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Resolves a held assignment without mutating the authoritative kernel.</summary>
internal sealed class ProviderHoldAgentSubstitution(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight,
    Func<DateTimeOffset>? utcNow = null)
{
    internal sealed record CandidateRejection(AgentId AgentId, IReadOnlyList<string> Findings);

    internal abstract record Result
    {
        internal sealed record None(IReadOnlyList<string> Findings,
            IReadOnlyList<CandidateRejection>? Rejections = null, string? HoldIdentity = null) : Result;
        internal sealed record WaitOnPin(IReadOnlyList<string> Findings) : Result;
        internal sealed record Substitute(AgentDefinition Agent, string HeldBindingKey, string EvidenceReceipt,
            ProviderSubscriptionCooldown? SubscriptionCooldown = null,
            IReadOnlyList<CandidateRejection>? Rejections = null, string? HoldIdentity = null) : Result;
    }

    internal Result Resolve(Goal goal, TaskSpec task)
    {
        if (task.Status != WorkTaskStatus.Assigned || task.LastProcess is { IsRunning: true } ||
            !agents.Any(agent => agent.Id == task.AssignedAgentId) ||
            goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderHoldSubstituted))
            return new Result.None([]);

        var assigned = preflight(goal, task, kernel.ProviderHoldScope);
        var hold = assigned.ErrorCode == WorkerProfileDispatcher.ProviderBudgetExhaustionErrorCode
            ? assigned.BlockedProviderHold : null;
        var now = UtcNow;
        var cooldown = assigned.BlockedSubscriptionCooldown;
        if (assigned.Allowed || (hold is null && cooldown is null)) return new Result.None(assigned.Findings);
        if (IsPinned(goal, task.Id)) return new Result.WaitOnPin(assigned.Findings);

        var holdIdentity = hold is not null
            ? $"{hold.BindingKey}|{hold.SourceGoalId}|{hold.SourceTaskId}|{hold.PlacedAt:O}|{hold.EvidenceReceipt}"
            : $"{cooldown!.BindingKey}|{cooldown.SourceTaskId}|{cooldown.DispatchStartedAt:O}|{cooldown.RetryAfter:O}|{cooldown.EvidenceReceipt}";
        List<CandidateRejection> rejections = [];
        // Reassignment exists only on this scratch view; every candidate uses the real preflight.
        foreach (var candidate in agents)
        {
            if (candidate.Status != AgentStatus.Available || candidate.Role != task.RequiredRole ||
                candidate.Id == task.AssignedAgentId || !AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy))
                continue;
            var scratch = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot(), new SubstitutionClock(now));
            scratch.ProviderHoldHistory.CopyFrom(kernel.ProviderHoldHistory);
            var view = scratch.ReassignTaskAgent(goal.Id, task.Id, candidate);
            var check = preflight(scratch.GetGoal(goal.Id), view, scratch.ProviderHoldScope);
            if (check.Allowed)
                return hold is not null && (cooldown is null ||
                        (hold.BindingIdentity with { Model = null }) != cooldown.BindingIdentity)
                    ? new Result.Substitute(candidate, hold.BindingKey, hold.EvidenceReceipt, null, rejections, holdIdentity)
                    : new Result.Substitute(candidate, cooldown!.BindingKey, cooldown.EvidenceReceipt, cooldown, rejections, holdIdentity);
            var blocked = check.Findings.Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)).ToArray();
            rejections.Add(new(candidate.Id, blocked.Length > 0 ? blocked : check.Findings));
        }
        return new Result.None(assigned.Findings, rejections, holdIdentity);
    }

    internal void ApplyBatch(Goal goal, IEnumerable<TaskId> taskIds)
    {
        foreach (var taskId in taskIds)
        {
            goal = kernel.GetGoal(goal.Id);
            var task = kernel.GetTask(goal.Id, taskId);
            var revision = task.ConductorRoutingRevision;
            if (DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length > 0) continue;
            var result = Resolve(goal, task);
            if (result is Result.Substitute { SubscriptionCooldown: { } selected } &&
                (selected.RetryAfter <= UtcNow ||
                 preflight(kernel.GetGoal(goal.Id), kernel.GetTask(goal.Id, taskId), kernel.ProviderHoldScope)
                     .BlockedSubscriptionCooldown != selected)) continue;
            goal = kernel.GetGoal(goal.Id);
            if (!ReferenceEquals(task, kernel.GetTask(goal.Id, taskId)) ||
                task.Status != WorkTaskStatus.Assigned || task.LastProcess is { IsRunning: true } ||
                task.ConductorRoutingRevision != revision || IsPinned(goal, taskId) ||
                DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length > 0)
                continue;
            if (result is Result.None none)
                RecordCandidateFindings(goal, taskId, none.HoldIdentity, none.Rejections, substituted: false);
            if (result is Result.Substitute substitute)
            {
                if (substitute.SubscriptionCooldown is { } cooldown && cooldown.RetryAfter <= UtcNow) continue;
                kernel.SubstituteProviderHeldTaskAgent(goal.Id, taskId, substitute.Agent,
                    substitute.HeldBindingKey, substitute.EvidenceReceipt, substitute.SubscriptionCooldown);
                RecordCandidateFindings(goal, taskId, substitute.HoldIdentity, substitute.Rejections, substituted: true);
            }
        }
    }

    private void RecordCandidateFindings(Goal goal, TaskId taskId, string? holdIdentity,
        IReadOnlyList<CandidateRejection>? rejections, bool substituted)
    {
        if (holdIdentity is null || rejections is not { Count: > 0 }) return;
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(holdIdentity)));
        var marker = $"Provider hold candidate review [{digest}]; {(substituted ? "passed over" : "rejected candidates")}: ";
        if (goal.Timeline.Any(evt => evt.TaskId == taskId && evt.Kind == ProgressKind.TaskNote &&
            evt.Message.StartsWith(marker, StringComparison.Ordinal))) return;
        kernel.RecordTaskNote(goal.Id, taskId, marker + string.Join("; ", rejections.Select(rejection =>
            $"seat '{rejection.AgentId.Value}': {string.Join(" | ", rejection.Findings)}")),
            correctionSource: CriteriaCorrectionSource.AgentIntent);
    }

    internal static bool CanAttemptSubscriptionSubstitution(Goal goal, TaskSpec task,
        IReadOnlyList<AgentDefinition> agents, DateTimeOffset now) =>
        task.Status == WorkTaskStatus.Assigned && task.LastProcess is not { IsRunning: true } &&
        !IsPinned(goal, task.Id) &&
        !goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.ProviderHoldSubstituted) &&
        DispatchReadinessRules.BuildIncompleteEarlierStageDependencies(goal, task).Length == 0 &&
        agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId) is { } assigned &&
        TryGetSubscriptionCooldown(goal, task, assigned, now, out var cooldown) &&
        agents.Any(candidate => candidate.Status == AgentStatus.Available &&
            candidate.Role == task.RequiredRole && candidate.Id != assigned.Id &&
            AgentExecutionPolicies.AllowsSubscription(candidate.ExecutionPolicy) &&
            (!candidate.Model.ProviderName.Equals(assigned.Model.ProviderName, StringComparison.OrdinalIgnoreCase) ||
             cooldown.ModelName is not null && !cooldown.AppliesToModel(ProviderLimitSignals.GetSubscriptionModelName(candidate))));

    internal static bool TryGetSubscriptionCooldown(Goal goal, TaskSpec task, AgentDefinition assigned,
        DateTimeOffset now, out ProviderSubscriptionCooldown cooldown)
    {
        if (ProviderLimitSignals.TryGetTaskSubscriptionCooldown(task, now, out cooldown) &&
            cooldown.ProviderName.Equals(assigned.Model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            // This task's stored deferral was validated against the actual dispatched model.
            // Role/complexity routing can make that model differ from its assigned seat's profile.
            (assigned.Id == task.AssignedAgentId ||
             cooldown.AppliesToModel(ProviderLimitSignals.GetSubscriptionModelName(assigned))))
            return true;
        // Planning and offline repair use this as a preliminary hint. Substitution uses
        // the cooldown selected by preflight for the actual provider, model and credential.
        return ProviderLimitSignals.TryGetProviderSubscriptionCooldown(goal, task.Id,
            assigned.Model.ProviderName,
            assigned.Id == task.AssignedAgentId ? null : ProviderLimitSignals.GetSubscriptionModelName(assigned),
            now, out cooldown);
    }

    private static bool IsPinned(Goal goal, TaskId taskId) => OfflineAssignedAgentRedelegation.IsOperatorPin(goal, taskId);

    private DateTimeOffset UtcNow => utcNow?.Invoke() ?? DateTimeOffset.UtcNow;

    private sealed class SubstitutionClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
