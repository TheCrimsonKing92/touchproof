using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SelectedExecutionBackstopGateRunner(ConductorDriver driver) : ISelectedExecutionBackstopRunner
{
    public Task<SelectedExecutionBackstopOutcome> RunAsync(SelectedExecutionBackstopRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => Run(request, cancellationToken), cancellationToken);

    private SelectedExecutionBackstopOutcome Run(SelectedExecutionBackstopRequest request, CancellationToken cancellationToken)
    {
        var workspace = driver._cohortWorkspace ?? throw new InvalidOperationException("Backstop workspace is unavailable.");
        var verifier = driver._cohortAcceptanceVerifier ?? throw new InvalidOperationException("Backstop verifier is unavailable.");
        var root = AcceptanceOwnerResultsRoot.ResolveOwnerResultsRootForCandidate(workspace.ExecutionDirectory);
        // Short like train checkouts: a 41-character name pushed nested test worktrees past git's path limit.
        var checkout = Path.Combine(root, ".orchestrator-worktrees", "b-" + request.Id[..Math.Min(12, request.Id.Length)]);
        cancellationToken.ThrowIfCancellationRequested();
        // A restarted request reuses its identity but never trusts a previous checkout's contents.
        if (Directory.Exists(checkout))
        {
            var stale = GitCli.Run(workspace.ExecutionDirectory, "worktree", "remove", "--force", checkout);
            if (!stale.Succeeded || stale.DrainTimedOut) return SelectedExecutionBackstopOutcome.Infrastructure("checkout-cleanup-failed");
        }
        var added = GitCli.Run(workspace.ExecutionDirectory, "worktree", "add", "--detach", "--quiet", checkout, request.ToCommit);
        try
        {
            if (!added.Succeeded || added.DrainTimedOut)
                return SelectedExecutionBackstopOutcome.Infrastructure($"checkout-add-exit-{added.ExitCode}");
            if (GoalWorktrees.ResolveRequiredRef(checkout, "HEAD") != request.ToCommit)
                return SelectedExecutionBackstopOutcome.Infrastructure("checkout-revision-mismatch");
            AcceptanceVerificationResult result;
            using (var lease = driver.CohortPartitionStableSlotLeaseSource is { } source
                ? source("selected-execution-backstop:" + request.Id, cancellationToken)
                : driver.ParallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease("selected-execution-backstop:" + request.Id, cancellationToken))
            {
                result = AcceptanceExecutionRunner.RunAttempt(verifier, checkout, null, changedFiles: null,
                    lease.Environment.BuildPermitIndex, lease, cancellationToken,
                    CreateExecutionOptions(new ConductEventLogWriter(workspace.ConductEventsLogPath), request,
                        workspace.ProjectHomeDirectoryOrNull, workspace.IntegrationBranch));
            }
            return Classify(result, SelectedExecutionBackstopFailureRerun.Create(
                driver, verifier, workspace, checkout, request, cancellationToken));
        }
        finally
        {
            if (Directory.Exists(checkout))
            {
                try
                {
                    var removed = GitCli.Run(workspace.ExecutionDirectory, "worktree", "remove", "--force", checkout);
                    if (!removed.Succeeded || removed.DrainTimedOut) ReportCleanup($"exit={removed.ExitCode}");
                }
                catch (Exception ex) { ReportCleanup($"{ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // Cleanup diagnostics survive without replacing an already completed gate verdict.
        void ReportCleanup(string detail)
        {
            try { Console.WriteLine($"SELECTED_EXECUTION_BACKSTOP_CLEANUP request={request.Id} path={checkout} detail={detail}"); }
            catch (IOException) { }
        }
    }

    internal static AcceptanceRunExecutionOptions CreateExecutionOptions(ConductEventLogWriter writer,
        SelectedExecutionBackstopRequest request, string? projectHomeDirectory, string integrationBranch) => new(
            RemoteLaneEventSink: detail => ConductorDriver.AppendRemoteLaneEvent(writer, null, detail),
            GateRunIdentity: request.Id, CohortRemoteLanes: true,
            ProjectHomeDirectory: projectHomeDirectory, IntegrationBranch: integrationBranch);

    // A red returned to the coordinator carries only failures confirmed before checkout cleanup.
    internal static SelectedExecutionBackstopOutcome Classify(AcceptanceVerificationResult result,
        SelectedExecutionBackstopFailureRerun rerun) => rerun.Confirm(Classify(result));

    internal static SelectedExecutionBackstopOutcome Classify(AcceptanceVerificationResult result)
    {
        if (result.Skipped || result.ExitCode is null || result.Checks is not { Count: > 0 })
            return SelectedExecutionBackstopOutcome.Infrastructure("verification-did-not-complete: " + result.OutputTail);
        var failed = result.Checks.Where(check => !check.Passed && !check.Advisory).ToArray();
        if (failed.Length > 0 && failed.All(check =>
                check.FailureCauseEvidence?.Cause == AcceptanceFailureCause.EnvironmentalApparatus ||
                AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification)))
            return SelectedExecutionBackstopOutcome.Infrastructure(string.Join(',', failed.Select(check =>
                check.FailureCauseEvidence?.Evidence ?? check.FailureClassification)));
        if (result.Passed && result.ExitCode == 0 && failed.Length == 0) return SelectedExecutionBackstopOutcome.Green;
        if (failed.Length == 0) return SelectedExecutionBackstopOutcome.Infrastructure("inconsistent-gate-verdict");
        return new("red", failed.SelectMany(check => (check.FailingTestIdentities ?? [])
                .Concat((check.FailingTestAttributions ?? []).Select(attribution => attribution.TestIdentity))).Distinct(StringComparer.Ordinal).ToArray(),
            failed.Select(check => check.Name).ToArray());
    }
}
