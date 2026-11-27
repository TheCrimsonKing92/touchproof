using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum MainFullVerificationRerunStatus { Confirmed, Unconfirmed, Unavailable }

internal sealed record MainFullVerificationRerun(MainFullVerificationRerunStatus Status, string? Detail = null);

internal static class MainFullVerificationConfirmation
{
    internal static MainFullVerificationResult Confirm(IReadOnlyList<MainFullVerificationFailure> failures,
        Func<string, CancellationToken, MainFullVerificationRerun> rerun, CancellationToken cancellationToken)
    {
        var confirmed = new List<MainFullVerificationFailure>();
        var unconfirmed = new List<string>();
        var unavailable = new List<string>();
        foreach (var failure in failures.DistinctBy(failure => failure.Test, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            MainFullVerificationRerun result;
            try
            {
                result = rerun(failure.Test, cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = new(MainFullVerificationRerunStatus.Unavailable, ex.GetType().Name);
            }

            switch (result.Status)
            {
                case MainFullVerificationRerunStatus.Confirmed:
                    confirmed.Add(failure);
                    break;
                case MainFullVerificationRerunStatus.Unconfirmed:
                    unconfirmed.Add(failure.Test);
                    break;
                default:
                    unavailable.Add($"{failure.Test} ({result.Detail ?? "rerun evidence unavailable"})");
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return unavailable.Count > 0
            ? new("incomplete", $"confirmation-unavailable: {string.Join(", ", unavailable)}", Unconfirmed: unconfirmed)
            : new(confirmed.Count > 0 ? "failed" : MainFullVerificationResult.PassedWithUnconfirmed,
                Failures: confirmed, Unconfirmed: unconfirmed);
    }

    internal static MainFullVerificationRerun RunFocused(IGoalAcceptanceVerifier verifier, string checkout,
        DotnetBuildEnvironmentLease lease, string integrationBranch, string? projectHome,
        IReadOnlyList<AcceptanceCheckResult> checks, string test, CancellationToken cancellationToken)
    {
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect([test],
            checks.Where(check => !check.Passed && !check.Advisory).ToArray());
        if (selection is null)
            return new(MainFullVerificationRerunStatus.Unavailable, "focused selection unavailable");

        // Read test rows while the focused execution owner still holds its artifacts.
        MainFullVerificationRerun confirmation = new(MainFullVerificationRerunStatus.Unavailable);
        AcceptanceExecutionRunner.RunFocusedVerification(verifier, checkout, goalId: null, selection.Request,
            lease.Environment.BuildPermitIndex, lease, runBaselineArm: false, cancellationToken, integrationBranch,
            projectHomeDirectory: projectHome, observeFocusedResult: result => confirmation = Classify(result, selection, test));
        return confirmation;
    }

    internal static MainFullVerificationRerun Classify(FocusedEvidenceRunResult result,
        ConductorCohortFocusedSelection selection, string test)
    {
        var focused = ConductorAcceptanceCohortFocusedAttribution.Interpret(result, selection);
        if (focused.Kind != ConductorCohortFocusedPassKind.Executed ||
            result.OutcomeReason == FindingEvidenceOutcomeReason.CandidateInconclusive)
            return new(MainFullVerificationRerunStatus.Unavailable, focused.Detail ?? result.Summary);

        var outcomes = focused.TestResultPaths.SelectMany(ProspectiveEvidenceTrxRows.Read)
            .Where(row => AcceptanceTrxTestIdentityResolver.NormalizeSelector(row.FullIdentity) == test)
            .Select(row => row.Outcome?.Trim()).ToArray();
        if (!focused.AllChecksPassed && outcomes.Contains("Failed", StringComparer.OrdinalIgnoreCase))
            return new(MainFullVerificationRerunStatus.Confirmed);
        if (focused.AllChecksPassed && outcomes.Length > 0 &&
            outcomes.All(outcome => string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase)))
            return new(MainFullVerificationRerunStatus.Unconfirmed);
        return new(MainFullVerificationRerunStatus.Unavailable, "focused result lacks a decisive outcome for the test");
    }
}
