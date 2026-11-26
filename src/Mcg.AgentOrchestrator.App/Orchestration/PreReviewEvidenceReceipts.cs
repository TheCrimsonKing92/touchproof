using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns current-candidate pre-review receipt coverage, reuse, and recording.
// The driver supplies its authoritative recording callback; this helper owns no kernel state.
internal static class PreReviewEvidenceReceipts
{
    internal const string FindingRoundFingerprint = "pre-review-stage";

    private static readonly Regex ClassTokenPattern = new(
        @"(?<![A-Za-z0-9_!])FullyQualifiedName\s*~\s*([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static PreReviewEvidenceReceipt Record(
        Action<GoalId, TaskId, PreReviewEvidenceReceipt> record,
        Goal goal,
        TaskSpec reviewerTask,
        PreReviewEvidenceContext context,
        int round,
        PreReviewEvidenceDisposition disposition,
        IReadOnlyList<AcceptanceCheckResult> checks,
        IReadOnlyList<string> failingTests,
        string? evidencePointer,
        IReadOnlyList<string>? evidenceTimeoutChecks = null,
        (string? TreeSha, string? ManifestIdentity) candidateIdentity = default,
        string? sourceAttemptId = null)
    {
        var receipt = new PreReviewEvidenceReceipt(
            goal.Id.Value,
            round,
            context.CandidateSha!,
            context.SelectedFocusedTests,
            disposition,
            checks.Count(check => check.Passed),
            checks.Count(check => !check.Passed),
            checks.Select((check, index) => new PreReviewEvidenceCheckReceipt(
                check.Name,
                ResolvePreReviewReceiptTarget(context, check.Name, index, checks.Count),
                check.Passed,
                check.ExitCode,
                check.ArtifactsPath,
                check.TestResultPaths)).ToArray(),
            failingTests,
            context.MappingReason,
            evidencePointer,
            DateTimeOffset.UtcNow,
            Advisories: FindExtraneousChecks(context, checks),
            EvidenceTimeoutChecks: evidenceTimeoutChecks,
            CandidateTreeSha: candidateIdentity.TreeSha,
            ManifestIdentity: candidateIdentity.ManifestIdentity,
            SourceAttemptId: sourceAttemptId);
        record(goal.Id, reviewerTask.Id, receipt);
        return receipt;
    }

    internal static (string? TreeSha, string? ManifestIdentity) ResolveCandidateIdentity(
        string? executionDirectory, GoalId goalId, string candidateSha, string integrationBranch, string? projectHomeDirectory)
    {
        if (executionDirectory is null)
            return default;

        try
        {
            var worktree = GoalWorktrees.TryResolve(executionDirectory, goalId);
            if (worktree is null || !string.Equals(GoalAcceptanceVerifier.ResolveGitScalar(worktree, "rev-parse", "HEAD"),
                    candidateSha, StringComparison.OrdinalIgnoreCase))
                return default;

            var tree = GoalAcceptanceVerifier.ResolveGitScalar(worktree, "rev-parse", candidateSha + "^{tree}");
            if (string.IsNullOrWhiteSpace(tree))
                return default;

            var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktree, integrationBranch);
            var settings = AcceptanceGateEngineSettings.Load(worktree, projectHomeDirectory);
            // The focused VSTest builder can intersect the displayed selector with a declared filter.
            var focusedFilterIsConstrained = VstestFocusedEvidenceCheckShape.TryResolve(
                    AcceptancePolicyShardPlanner.InfrastructureTestsProject, settings, out var declaration) &&
                declaration.Arguments.Any(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase));
            var manifest = focusedFilterIsConstrained ? null :
                GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(worktree, changedFiles, projectHomeDirectory);
            return string.Equals(GoalAcceptanceVerifier.ResolveGitScalar(worktree, "rev-parse", "HEAD"),
                candidateSha, StringComparison.OrdinalIgnoreCase) ? (tree, manifest) : default;
        }
        catch (Exception)
        {
            // An unresolved identity leaves the receipt usable for pre-review's existing checks.
            return default;
        }
    }

    internal static (string? TreeSha, string? ManifestIdentity) ResolveRecordedCandidateIdentity(
        string? executionDirectory, GoalId goalId, string candidateSha, string integrationBranch,
        string? projectHomeDirectory, string? executedManifestIdentity)
    {
        var identity = ResolveCandidateIdentity(executionDirectory, goalId, candidateSha, integrationBranch, projectHomeDirectory);
        return string.Equals(identity.ManifestIdentity, executedManifestIdentity, StringComparison.Ordinal)
            ? identity : (identity.TreeSha, null);
    }

    internal static bool TryReuse(
        TaskSpec reviewerTask,
        string goalId,
        string? candidateSha,
        IReadOnlyList<string> requestedSelections,
        out IReadOnlyList<PreReviewEvidenceReceipt> constituentReceipts)
    {
        constituentReceipts = [];
        if (string.IsNullOrWhiteSpace(candidateSha) || requestedSelections.Count == 0)
        {
            return false;
        }

        var currentCandidateReceipts = reviewerTask.PreReviewEvidenceHistory
            .Where(receipt =>
                string.Equals(receipt.GoalId, goalId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToArray();
        if (currentCandidateReceipts.Length == 0)
        {
            return false;
        }

        // The latest receipt for each selection is authoritative. An earlier green receipt must
        // never hide a later red, incomplete, or mapping-needed result for the same candidate.
        var latestReceipts = requestedSelections
            .Select(selection => currentCandidateReceipts.FirstOrDefault(receipt =>
                receipt.SelectedFocusedTests.Contains(selection, StringComparer.Ordinal)))
            .ToArray();
        if (latestReceipts.Any(receipt => receipt is null) ||
            latestReceipts.Any(receipt => !HasCompleteGreenCoverage(receipt!)))
        {
            return false;
        }

        constituentReceipts = latestReceipts
            .OfType<PreReviewEvidenceReceipt>()
            .Distinct()
            .ToArray();
        return constituentReceipts.Count > 0;
    }

    private static bool HasCompleteGreenCoverage(PreReviewEvidenceReceipt receipt) =>
        receipt.Disposition == PreReviewEvidenceDisposition.Green &&
        receipt.FailedCheckCount == 0 && receipt.FailingTestIdentities.Count == 0 &&
        receipt.SelectedFocusedTests.Count > 0 &&
        receipt.SelectedFocusedTests.Distinct(StringComparer.Ordinal).Count() == receipt.SelectedFocusedTests.Count &&
        receipt.PassedCheckCount == receipt.Checks.Count &&
        receipt.Checks.Count == receipt.SelectedFocusedTests.Count &&
        receipt.Checks.All(check => check.Passed && check.ExitCode == 0) &&
        receipt.SelectedFocusedTests.All(selection =>
            receipt.Checks.Count(check => string.Equals(check.Command, selection, StringComparison.Ordinal)) == 1);

    internal static void RecordReuse(
        Action<GoalId, TaskId, PreReviewEvidenceReceipt> record,
        Goal goal,
        TaskSpec reviewerTask,
        PreReviewEvidenceContext context,
        int round,
        IReadOnlyList<PreReviewEvidenceReceipt> constituents)
    {
        var evidencePointers = constituents
            .Select(receipt => receipt.EvidencePointer)
            .Where(pointer => !string.IsNullOrWhiteSpace(pointer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pointer => pointer, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var checks = context.SelectedFocusedTests.Select(selection => constituents
            .SelectMany(constituent => constituent.Checks)
            .First(check => string.Equals(check.Command, selection, StringComparison.Ordinal))).ToArray();
        var identity = constituents.FirstOrDefault();
        var sharedIdentity = identity is not null &&
            constituents.All(source =>
                string.Equals(source.CandidateTreeSha, identity.CandidateTreeSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(source.ManifestIdentity, identity.ManifestIdentity, StringComparison.Ordinal));
        var receipt = new PreReviewEvidenceReceipt(
            goal.Id.Value,
            round,
            context.CandidateSha!,
            context.SelectedFocusedTests,
            PreReviewEvidenceDisposition.Green,
            PassedCheckCount: checks.Length,
            FailedCheckCount: 0,
            // A reuse receipt must describe the original checks, never invent executions that
            // did not occur in this reviewer round.
            Checks: checks,
            FailingTestIdentities: [],
            MappingReason: context.MappingReason + "; reused green current-candidate evidence coverage.",
            EvidencePointer: evidencePointers.Length == 0 ? PreReviewEvidenceReceipt.SyntheticReuseAdvisory : string.Join(",", evidencePointers),
            RecordedAt: DateTimeOffset.UtcNow,
            Advisories: [PreReviewEvidenceReceipt.SyntheticReuseAdvisory],
            CandidateTreeSha: sharedIdentity ? identity!.CandidateTreeSha : null,
            ManifestIdentity: sharedIdentity ? identity!.ManifestIdentity : null);
        record(goal.Id, reviewerTask.Id, receipt);
    }

    internal static bool ValidateCoverage(
        PreReviewEvidenceContext context,
        FocusedEvidenceRunResult evidence,
        out string failure)
    {
        failure = string.Empty;
        if (context.SelectedFocusedTests.Count == 0)
        {
            return true;
        }

        var itemClasses = context.SelectedFocusedTests.Select(ExtractClassTokens).ToArray();
        if (itemClasses.All(classes => classes.Count > 0))
        {
            var traitExcludedClasses = (evidence.Coverage?.DroppedSelections ?? [])
                .Where(drop => drop.Reasons.Any(reason =>
                    reason.StartsWith("excluded-by-trait:", StringComparison.Ordinal)))
                .SelectMany(drop => ExtractClassTokens($"FullyQualifiedName~{drop.Selection}"));
            var coveredClasses = evidence.Checks.SelectMany(check => ExtractClassTokens(check.Name))
                .Concat(traitExcludedClasses)
                .ToHashSet(StringComparer.Ordinal);
            var missing = context.SelectedFocusedTests.Select((item, index) =>
            {
                var uncovered = itemClasses[index].Where(className =>
                    !coveredClasses.Contains(className)).ToArray();
                return uncovered.Length == 0
                    ? null
                    : $"item '{item}' uncovered {string.Join(", ", uncovered)}";
            }).OfType<string>().ToArray();
            if (missing.Length == 0)
            {
                return true;
            }

            failure = $"focused evidence missing selected classes: {string.Join("; ", missing)}";
            return false;
        }

        // Older whole-project and synthetic checks have no class identity to attribute.
        if (evidence.Checks.Count == context.SelectedFocusedTests.Count)
        {
            return true;
        }

        failure = $"focused evidence carries no FullyQualifiedName~ class identity; positional mapping needs one check per selected item (planned={context.SelectedFocusedTests.Count} actual={evidence.Checks.Count})";
        return false;
    }

    private static string ResolvePreReviewReceiptTarget(
        PreReviewEvidenceContext context,
        string checkName,
        int index,
        int checkCount)
    {
        var checkClasses = ExtractClassTokens(checkName);
        if (checkClasses.Count > 0 &&
            context.SelectedFocusedTests.All(item => ExtractClassTokens(item).Count > 0))
        {
            var matchingItems = context.SelectedFocusedTests.Where(item =>
                ExtractClassTokens(item).Overlaps(checkClasses)).ToArray();
            return matchingItems.Length > 0
                ? string.Join("; ", matchingItems)
                : "(unmapped: check covers no selected class)";
        }

        return checkCount == context.SelectedFocusedTests.Count
            ? context.SelectedFocusedTests[index]
            : "(unmapped: check/command cardinality mismatch)";
    }

    private static HashSet<string> ExtractClassTokens(string value) =>
        ClassTokenPattern.Matches(value)
            .Select(match => match.Groups[1].Value.Trim())
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string>? FindExtraneousChecks(
        PreReviewEvidenceContext context,
        IReadOnlyList<AcceptanceCheckResult> checks)
    {
        var selectedClasses = context.SelectedFocusedTests
            .SelectMany(ExtractClassTokens)
            .ToHashSet(StringComparer.Ordinal);
        if (context.SelectedFocusedTests.Count > 0 && selectedClasses.Count == 0)
        {
            return null;
        }

        var advisories = checks.Where(check =>
            ExtractClassTokens(check.Name) is { Count: > 0 } classes &&
            !classes.Overlaps(selectedClasses))
            .Select(check => $"extraneous-focused-check: {check.Name}")
            .ToArray();
        return advisories.Length == 0 ? null : advisories;
    }
}
