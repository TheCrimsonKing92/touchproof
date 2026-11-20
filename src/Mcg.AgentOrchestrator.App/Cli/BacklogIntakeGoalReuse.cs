using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogIntakeGoalReuse
{
    internal static bool Execute(
        CliExecutionContext context,
        BacklogIntakeItem item,
        BacklogItem backlogItem,
        Goal owner,
        GoalObjectivePlan? plan,
        GoalIntakePipelineRequest? pipelineRequest,
        bool forceReclaim,
        bool batch,
        Action<BacklogItem> validatePromotion,
        Action<Goal, BacklogItem> applyDependencies)
    {
        if (plan is not null)
        {
            if (TryCorrectPipeline(context, item, backlogItem, owner, plan, pipelineRequest,
                    forceReclaim, validatePromotion, applyDependencies))
            {
                if (!batch)
                    GoalTextView.PrintGoal(context.CurrentGoal!);
                return true;
            }
            GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(plan, owner);
        }
        context.CurrentGoal = owner;
        Console.WriteLine(batch
            ? $"Backlog slice '{item.Heading}' already has goal {owner.Id.Value[..8]}; no new goal created."
            : $"Backlog slice already has goal {owner.Id.Value[..8]}; no new goal created.");
        if (plan?.PipelineDecision.IsOverride == true)
            GoalObjectivePlanTextView.PrintGoalObjectivePlan(plan);
        if (!batch)
            GoalTextView.PrintGoal(owner);
        return false;
    }

    private static bool TryCorrectPipeline(
        CliExecutionContext context,
        BacklogIntakeItem item,
        BacklogItem backlogItem,
        Goal owner,
        GoalObjectivePlan plan,
        GoalIntakePipelineRequest? pipelineRequest,
        bool forceReclaim,
        Action<BacklogItem> validatePromotion,
        Action<Goal, BacklogItem> applyDependencies)
    {
        if (!forceReclaim || !HasPipelineMismatch(plan, owner))
            return false;

        var claim = new SourceBacklogClaimStore(context.Workspace.SqliteStatePath)
            .ResolveClaim(context.Kernel, item.Id) ?? throw new InvalidOperationException(
            $"BACKLOG_INTAKE_FORCE_RECLAIM_REFUSED backlogItem={item.Id} owner={owner.Id.Value} reason=source-claim-missing");
        if (!string.Equals(claim.OwnerGoalId, owner.Id.Value, StringComparison.Ordinal))
            throw new SourceBacklogClaimConflictException(item.Id, owner.Id.Value, claim.OwnerGoalId, claim.Version);

        var facts = GoalReplacementEvidence.Capture(context.Workspace, owner);
        var outcome = SourceBacklogClaimEligibility.Evaluate(GoalReplacementDisposition.ZeroWorkCorrection, facts);
        if (outcome != GoalReplacementOutcome.Succeeded)
            throw new InvalidOperationException(
                $"BACKLOG_INTAKE_FORCE_RECLAIM_REFUSED outcome={outcome} backlogItem={item.Id} owner={owner.Id.Value} claimVersion={claim.Version} refused_by={string.Join(',', facts.ListCorrectionBlockers())}");
        if (!context.HasGoalReplacementFinalizer)
            throw new InvalidOperationException(
                $"BACKLOG_INTAKE_FORCE_RECLAIM_REFUSED backlogItem={item.Id} owner={owner.Id.Value} reason=correction-finalizer-unavailable");

        validatePromotion(backlogItem);
        var epicId = EpicAtCreation.ResolveGoalEpic(context.Workspace, null, backlogItem.Id);
        var successor = GoalLifecycleCommands.CreateAndActivateGoal(
            context.Kernel, context.Agents, plan, context.Workspace, context.Providers, context.EventWriter);
        context.Kernel.SetGoalSourceBacklogItemLink(successor.Id, item.Id, claim.Coverage);
        applyDependencies(successor, backlogItem);
        context.FinalizeGoalReplacement(successor, new GoalReplacementCommand(
            owner.Id, Guid.NewGuid(), GoalReplacementDisposition.ZeroWorkCorrection,
            $"Correct backlog intake pipeline to {(pipelineRequest ?? GoalIntakePipelineRequest.Auto).ToCanonicalValue()}.",
            item.SuggestedObjective, claim.OwnerGoalId, claim.Version,
            (pipelineRequest ?? GoalIntakePipelineRequest.Auto).ToCanonicalValue(), ""));
        context.CurrentGoal = context.Kernel.GetGoal(successor.Id);
        EpicAtCreation.AssignAfterCommit(context.Workspace, PortfolioMemberKind.Goal, successor.Id.Value, epicId);
        Console.WriteLine($"Created goal from backlog slice '{item.Heading}'.");
        GoalObjectivePlanTextView.PrintGoalObjectivePlan(plan);
        return true;
    }

    internal static bool HasPipelineMismatch(GoalObjectivePlan plan, Goal owner)
    {
        try
        {
            GoalLifecycleCommands.EnsureRequestedPipelineMatchesPersistedGoal(plan, owner);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    internal static bool TryResolveClaimOwner(
        CliExecutionContext context,
        BacklogIntakeItem item,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
    {
        existingGoal = string.IsNullOrWhiteSpace(item.Id)
            ? null
            : CliCommandHandlers.FindAuthoritativeSourceGoal(context, item.Id);
        return existingGoal is not null;
    }

    internal static bool TryResolveIntakeOwner(
        CliExecutionContext context,
        BacklogIntakeItem item,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
    {
        existingGoal = null;
        if (string.IsNullOrWhiteSpace(item.Id))
            return false;

        var record = new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath).Get(item.Id);
        if (record is null || string.IsNullOrWhiteSpace(record.GoalId))
            return false;

        existingGoal = context.Kernel.Goals.FirstOrDefault(goal =>
            goal.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
        return existingGoal is not null;
    }
}
