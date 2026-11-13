using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SelectedExecutionBackstopFailureRerun(
    Func<ConductorCohortFocusedSelection, ConductorCohortFocusedPassResult> runFocused)
{
    internal static SelectedExecutionBackstopFailureRerun Create(ConductorDriver driver,
        IGoalAcceptanceVerifier verifier, OrchestratorWorkspace workspace, string checkout,
        SelectedExecutionBackstopRequest request, CancellationToken cancellationToken) => new(selection =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The original attempt released its execution lock; the focused run owns a fresh lease.
        var identity = "selected-execution-backstop-rerun:" + request.Id;
        using var lease = driver.CohortPartitionStableSlotLeaseSource is { } source
            ? source(identity, cancellationToken)
            : driver.ParallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLease(identity, cancellationToken);
        var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, checkout, goalId: null,
            selection.Request, lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, cancellationToken,
            integrationBranch: workspace.IntegrationBranch, projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull);
        return ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection);
    });

    internal SelectedExecutionBackstopOutcome Confirm(SelectedExecutionBackstopOutcome outcome)
    {
        if (outcome.Result != "red") return outcome;
        // Select methods to execute, but retain full row identities when confirming failures.
        var selectors = outcome.FailingTests.Select(AcceptanceTrxTestIdentityResolver.NormalizeSelector)
            .Distinct(StringComparer.Ordinal).ToArray();
        // Validate every batch before running any, so an unselectable later batch runs nothing.
        var selections = selectors.Chunk(AcceptanceFailureAttributionPlanner.MaxCandidateRerunIdentities)
            .Select(batch => ConductorAcceptanceCohortFocusedAttribution.TrySelect(batch, [])).ToArray();
        if (selections.Length == 0 || selections.Any(selection => selection is null))
            return SelectedExecutionBackstopOutcome.Infrastructure("rerun-unavailable: failing tests could not be selected");

        var repeated = new HashSet<string>(StringComparer.Ordinal);
        var allPassed = true;
        for (var index = 0; index < selections.Length; index++)
        {
            ConductorCohortFocusedPassResult pass;
            try { pass = runFocused(selections[index]!); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return SelectedExecutionBackstopOutcome.Infrastructure(
                    $"rerun-failed: {ex.GetType().Name}: {ex.Message} (batch {index + 1})");
            }
            if (pass.Kind != ConductorCohortFocusedPassKind.Executed)
                return SelectedExecutionBackstopOutcome.Infrastructure(
                    $"rerun-not-executed: {pass.Kind}: {pass.Detail} (batch {index + 1})");

            repeated.UnionWith(pass.FailingIdentities.Select(identity => identity.Trim()));
            allPassed &= pass.AllChecksPassed;
        }

        var originalMethods = selectors.ToHashSet(StringComparer.Ordinal);
        var hasUnexpectedFailures = repeated.Any(identity =>
            !originalMethods.Contains(AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity)));
        var confirmed = outcome.FailingTests.Where(identity => repeated.Contains(identity.Trim())).ToArray();
        if (!hasUnexpectedFailures && confirmed.Length > 0) return outcome with { FailingTests = confirmed };
        // A first-run failure that passes when run again is a flake, not a miss, so the range is covered.
        // A re-run that was incomplete or failed something else proves nothing and stays pending.
        return allPassed && repeated.Count == 0
            ? new("green", [], [], $"rerun-passed: originalCount={outcome.FailingTests.Count}; flaky={string.Join(',', outcome.FailingTests.Take(5))}")
            : SelectedExecutionBackstopOutcome.Infrastructure($"rerun-incomplete: no original failures confirmed; originalCount={outcome.FailingTests.Count}");
    }
}
