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
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect(selectors, []);
        if (selection is null)
            return SelectedExecutionBackstopOutcome.Infrastructure("rerun-unavailable: failing tests could not be selected");

        ConductorCohortFocusedPassResult pass;
        try { pass = runFocused(selection); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SelectedExecutionBackstopOutcome.Infrastructure($"rerun-failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (pass.Kind != ConductorCohortFocusedPassKind.Executed)
            return SelectedExecutionBackstopOutcome.Infrastructure($"rerun-not-executed: {pass.Kind}: {pass.Detail}");

        var repeated = pass.FailingIdentities.Select(identity => identity.Trim()).ToHashSet(StringComparer.Ordinal);
        var confirmed = outcome.FailingTests.Where(identity => repeated.Contains(identity.Trim())).ToArray();
        return confirmed.Length == 0
            ? SelectedExecutionBackstopOutcome.Infrastructure($"rerun-passed: no original failures confirmed; originalCount={outcome.FailingTests.Count}")
            : outcome with { FailingTests = confirmed };
    }
}
