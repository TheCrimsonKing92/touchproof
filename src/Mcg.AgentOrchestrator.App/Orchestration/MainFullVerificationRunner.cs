using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MainFullVerificationRunner(
    OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier, ConductorParallelAcceptanceAttemptCoordinator acceptance,
    SelectedExecutionBackstopStore landings)
{
    internal MainFullVerificationRun? TryReserve(string commit, CancellationToken cancellationToken)
    {
        var runId = RunId(commit);
        try
        {
            var lease = acceptance.AcquireCohortStableSlotLease(runId, cancellationToken,
                timeout: TimeSpan.Zero, holderLabel: $"main-full-verification:{commit[..Math.Min(12, commit.Length)]}");
            return new((previous, token) => Execute(commit, previous, lease, token), lease);
        }
        catch (DotnetBuildSlotsBusyException) { return null; }
    }

    internal static string RunId(string commit) => $"main-full-verification-{commit}";

    private MainFullVerificationResult Execute(string commit, string? lastPassed, DotnetBuildEnvironmentLease lease,
        CancellationToken cancellationToken)
    {
        var root = workspace.ExecutionDirectory;
        var checkout = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("main-full-verification-worktrees"),
            Guid.NewGuid().ToString("N"));
        using var qos = HostedVerifierRunQos.Begin(workspace);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var added = GitCli.Run(root, "worktree", "add", "--detach", "--quiet", checkout, commit);
            if (!added.Succeeded || added.DrainTimedOut)
                return new("incomplete", $"checkout-unavailable:exit={added.ExitCode}");
            if (!string.Equals(GoalWorktrees.ResolveRequiredRef(checkout, "HEAD"), commit, StringComparison.OrdinalIgnoreCase))
                return new("incomplete", "checkout-revision-mismatch");

            var verification = RunAttempt(verifier, checkout, commit, lease, cancellationToken,
                workspace.ProjectHomeDirectoryOrNull, workspace.IntegrationBranch);
            return Conclude(verification, tests => Attribute(tests, checkout, commit, lastPassed),
                (test, token) => MainFullVerificationConfirmation.RunFocused(verifier, checkout, lease,
                    workspace.IntegrationBranch, workspace.ProjectHomeDirectoryOrNull, verification.Checks ?? [], test, token),
                cancellationToken);
        }
        finally
        {
            if (Directory.Exists(checkout))
            {
                var removed = GitCli.Run(root, "worktree", "remove", "--force", checkout);
                if (!removed.Succeeded || removed.DrainTimedOut)
                    Console.Error.WriteLine($"MAIN_FULL_VERIFICATION_CLEANUP_FAILED commit={commit} exit={removed.ExitCode} path={checkout}");
            }
        }
    }

    internal static AcceptanceVerificationResult RunAttempt(IGoalAcceptanceVerifier verifier, string checkout,
        string commit, DotnetBuildEnvironmentLease? lease, CancellationToken cancellationToken,
        string? projectHome, string? integrationBranch) =>
        AcceptanceExecutionRunner.RunAttempt(verifier, checkout, goalId: null, changedFiles: null,
            lease?.Environment.BuildPermitIndex, lease, cancellationToken,
            new AcceptanceRunExecutionOptions(RunId: RunId(commit), ProjectHomeDirectory: projectHome,
                IntegrationBranch: integrationBranch, PinnedMainCommit: commit));

    internal static MainFullVerificationResult Conclude(AcceptanceVerificationResult verification,
        Func<IReadOnlyList<string>, IReadOnlyList<MainFullVerificationFailure>> attribute,
        Func<string, CancellationToken, MainFullVerificationRerun> rerun, CancellationToken cancellationToken)
    {
        var result = Interpret(verification, attribute);
        if (result.Failures is not { Count: > 0 } failures)
            return result;

        var confirmation = MainFullVerificationConfirmation.Confirm(failures, rerun, cancellationToken);
        if (result.Outcome != "incomplete")
            return confirmation;

        return new("incomplete", confirmation.Detail is null ? result.Detail : $"{result.Detail}. {confirmation.Detail}",
            Unconfirmed: confirmation.Unconfirmed);
    }

    internal static MainFullVerificationResult Interpret(AcceptanceVerificationResult verification,
        Func<IReadOnlyList<string>, IReadOnlyList<MainFullVerificationFailure>> attribute)
    {
        var lanePlan = verification.GateLanePlanPath is { } path && verification.AttemptId is { } attemptId
            ? new OwnerConsoleGateLanePlanReader().Read(path, attemptId) : null;
        if (lanePlan?.SelectedExecution is { Applied: true } or { StructuralCoverageScoped: true } ||
            lanePlan?.Skipped.Any(skip => skip.Reason is AcceptanceGateLaneSkip.NoBuildSystemChange or
                AcceptanceGateLaneSkip.OutsideChangeScope or AcceptanceGateLaneSkip.OutsideDependencyClosure or
                AcceptanceGateLaneSkip.OutsideAttributionScope or AcceptanceGateLaneSkip.ReplacedByFocusedCheck or
                AcceptanceGateLaneSkip.LaneNotSelected or AcceptanceGateLaneSkip.ClassNotSelected) == true)
            return new("incomplete", "unexpected-impact-selection");
        if (verification.Skipped) return new("incomplete", "verification-skipped");

        var checks = verification.Checks ?? [];
        var failed = checks.Where(check => !check.Passed && !check.Advisory).ToArray();
        if (verification.Passed)
        {
            var passed = checks.Where(check => check.Passed).Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
            return lanePlan is not null && verification.RequiredTestCheckNames.Count > 0 &&
                verification.RequiredTestCheckNames.All(passed.Contains) && failed.Length == 0
                ? new("passed") : new("incomplete", "required-test-checks-unproven");
        }

        var tests = new SortedSet<string>(StringComparer.Ordinal);
        var unnamedChecks = new List<string>();
        foreach (var check in failed)
        {
            var names = ReadFailedTests(check).ToArray();
            if (names.Length == 0)
                unnamedChecks.Add(check.Name);
            tests.UnionWith(names);
        }

        var detail = unnamedChecks.Count > 0
            ? $"fatal-test-identity-unavailable: {string.Join(", ", unnamedChecks)}" : null;
        if (tests.Count == 0)
            return new("incomplete", detail ?? "fatal-test-identity-unavailable");

        return new(detail is null ? "failed" : "incomplete", detail, attribute(tests.ToArray()));
    }

    private static IEnumerable<string> ReadFailedTests(AcceptanceCheckResult check)
    {
        var paths = GroupedReceiptTestResultPaths.Resolve(
            new(false, false, check.ExitCode, null, Checks: [check]), check.TestResultPaths ?? []).Paths;
        var reads = paths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (reads.Any(read => read.Status != AcceptanceTrxReadStatus.Readable)) return [];
        return reads.SelectMany(read => read.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                !string.IsNullOrWhiteSpace(failure.TestName))
            .Select(failure => AcceptanceTrxTestIdentityResolver.NormalizeSelector(failure.TestName!))
            .Where(selector => !string.IsNullOrWhiteSpace(selector));
    }

    private IReadOnlyList<MainFullVerificationFailure> Attribute(
        IReadOnlyList<string> tests, string checkout, string commit, string? lastPassed)
    {
        try
        {
            var knownLandings = landings.ReadAsync().GetAwaiter().GetResult()?.RangeLandings ?? [];
            return MainFullVerificationLandingAttribution.Resolve(checkout, commit, lastPassed, tests, knownLandings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            return tests.Select(test => new MainFullVerificationFailure(test)).ToArray();
        }
    }
}

internal sealed record MainFullVerificationRun(
    Func<string?, CancellationToken, MainFullVerificationResult> Execute, IDisposable? Lease = null);
