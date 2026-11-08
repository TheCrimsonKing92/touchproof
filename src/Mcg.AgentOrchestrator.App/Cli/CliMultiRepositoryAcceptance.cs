using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliMultiRepositoryAcceptance
{
    // True means this command belongs to the repository-set path, never the merge/cleanup path.
    internal static bool Apply(CliExecutionContext context, Goal goal, bool runGate = true)
    {
        if (!MultiRepositoryGoal.Applies(goal)) return false;
        var gate = new MultiRepositoryAcceptanceGate(context.Workspace,
            context.ProjectRegistry ?? OrchestratorProjectRegistry.CreateDefault(), context.AcceptanceVerifier);
        var state = GoalLifecycle.ResolveState(goal);
        var landings = new MultiRepositoryLandingRecordStore(context.Workspace.OrchestratorDirectory);
        if (landings.ReadFailure(goal.Id) is { } landingFailure)
        {
            var failedHold = MultiRepositoryLandingFailedHold.Create(state, landingFailure);
            ConductorLandingBlockerRecorder.Observe(context.Kernel, goal, failedHold, DateTimeOffset.UtcNow, []);
            Console.WriteLine(failedHold.Reason);
            return true;
        }
        var owner = $"multi-repository-acceptance:{Environment.ProcessId}:{Guid.NewGuid():N}";
        using var lease = new ReconcileSweepRemediationStore(context.Workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30));
        if (lease is null)
        {
            Console.WriteLine("BLOCKER multi-repository acceptance is already running.");
            return true;
        }
        var result = runGate && AcceptancePrecheck.HasCompletedPassedVerificationForAllTasks(goal)
            ? gate.Run(goal, state, () => RunHome(context, goal)) : gate.Inspect(goal, state);
        if (result.Landable is not null || result.Hold is not null)
        {
            var hold = result.Hold ?? MultiRepositoryGoal.Routed(goal, state);
            if (result.Landable is not null)
                context.Kernel.ClearAcceptanceFailure(goal.Id);
            ConductorLandingBlockerRecorder.Observe(context.Kernel, goal, hold, DateTimeOffset.UtcNow, []);
            Console.WriteLine(hold.Reason);
        }
        else
        {
            var failure = result.Failure!;
            context.Kernel.RecordAcceptanceFailure(goal.Id, failure.FailedChecks!);
            var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer && task.Status == WorkTaskStatus.Completed);
            if (developer is not null)
            {
                var feedback = failure.RequiredUnmetCriteria.Select(check => check.Name + ": " + check.OutputTail).ToArray();
                context.Kernel.RecordCriterionRetryFeedback(goal.Id, developer.Id, feedback);
                context.Kernel.RetryTaskAutomatically(goal.Id, developer.Id, string.Join(Environment.NewLine, feedback), RetryCause.CriterionEvidenceOwnerMismatch);
            }
            Console.WriteLine(failure.FailureDetail);
        }
        new MultiRepositoryLandingHoldAttention(CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory),
            current => landings.ReadFailure(current.Id)).Reconcile(goal);
        return true;
    }

    private static AcceptanceVerificationSummary RunHome(CliExecutionContext context, Goal goal)
    {
        var path = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id)
            ?? throw new InvalidOperationException("default:missing-worktree");
        var verification = AcceptanceExecutionRunner.RunAttempt(context.AcceptanceVerifier, path, goal.Id,
            context.Worktrees.GetChangedFiles(path), null, null, CancellationToken.None,
            new AcceptanceRunExecutionOptions(ProjectHomeDirectory: context.Workspace.ProjectHomeDirectoryOrNull,
                IntegrationBranch: context.Workspace.IntegrationBranch));
        var failed = verification.Checks?.Where(check => !check.Passed).ToArray() ?? [];
        return AcceptanceVerificationSummary.FromVerification(verification, failed, failed.Select(check => check.Name).ToArray(),
            null, null, verification.TestResultPaths, null, null);
    }
}
