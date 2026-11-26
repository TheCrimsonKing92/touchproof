using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the translation from an impact plan to broker-ready pre-review evidence context.
internal static class PreReviewEvidenceContextBuilder
{
    internal static PreReviewEvidenceContext WithFailedGateClasses(
        PreReviewEvidenceContext context, IReadOnlyList<(string Alias, string ClassName)> classes) =>
        WithAdditionalClasses(context, classes, "failed-gate coverage");

    private static PreReviewEvidenceContext WithAdditionalClasses(
        PreReviewEvidenceContext context, IReadOnlyList<(string Alias, string ClassName)> classes, string reason)
    {
        if (classes.Count == 0 || context.MappingNeedsInput || context.RequiresSourceCleanup) return context;

        var requests = new List<string>();
        foreach (var group in classes.Distinct().OrderBy(item => item.Alias, StringComparer.Ordinal)
                     .ThenBy(item => item.ClassName, StringComparer.Ordinal).GroupBy(item => item.Alias))
        {
            var clauses = group.Select(item => $"FullyQualifiedName~{item.ClassName}")
                .Where(clause => !context.SelectedFocusedTests.Any(selection =>
                    PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(selection, out var alias) && alias == group.Key &&
                    Regex.Matches(selection, @"(?<![A-Za-z0-9_!])FullyQualifiedName\s*~\s*([A-Za-z_][A-Za-z0-9_.]*)",
                        RegexOptions.CultureInvariant).Any(match => "FullyQualifiedName~" + match.Groups[1].Value == clause)))
                .ToArray();
            if (clauses.Length > 0 && PreReviewFocusedRequestSplitter.TrySplitRequestItems(
                    group.Key, string.Join('|', clauses), out var items))
                requests.AddRange(items);
        }
        if (requests.Count == 0) return context;

        // Use the broker items as selections, as Build does, so each check retains reusable coverage.
        return context with
        {
            SelectedFocusedTests = context.SelectedFocusedTests.Concat(requests).ToArray(),
            FocusedRequest = string.Join("; ", new[] { context.FocusedRequest }.OfType<string>()
                .Where(request => !string.IsNullOrWhiteSpace(request)).Concat(requests)),
            NoApplicableTests = false,
            MappingReason = context.MappingReason + $"; {reason}: " + string.Join("; ", requests)
        };
    }

    internal static PreReviewEvidenceContext Build(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan,
        Action<string>? log = null, Goal? goal = null, string? worktreePath = null)
    {
        var context = BuildMappedPreReviewEvidenceContext(candidateSha, changeSummary, plan, log ?? Console.WriteLine);
        context = context with
        {
            MappingReason = context.NoApplicableTests
                ? DescribeUnmappedTestSources(context.MappingReason, changeSummary, plan)
                : context.MappingReason,
            TestImpactDegradation = plan.ReverseDependencyDegradation,
            TestImpactHeadroom = plan.ReverseDependencyHeadroom
        };

        return goal is not null && worktreePath is not null && ReverseDependencyBreadthBound.IsDeferred(plan) &&
            !context.MappingNeedsInput && !context.RequiresSourceCleanup
            ? WithNamedClasses(context, goal, worktreePath)
            : context;
    }

    private static PreReviewEvidenceContext WithNamedClasses(
        PreReviewEvidenceContext context, Goal goal, string worktreePath)
    {
        var objectiveNames = PreTesterNamedTestClasses.Harvest(goal.Objective).ToHashSet(StringComparer.Ordinal);
        var obligationNames = GateTestObligationEffectiveSelectors.ForMembers([goal], [goal.Id])
            .SelectMany(PreTesterNamedTestClasses.Harvest);
        var names = objectiveNames.Concat(obligationNames).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return context;

        var objectiveClasses = new List<(string Alias, string ClassName)>();
        var obligationClasses = new List<(string Alias, string ClassName)>();
        var resolved = DeveloperDeferredTestSelections.ResolveNames(worktreePath, names, requireUniqueSourceFile: true);
        foreach (var selection in resolved.Selections)
        {
            if (PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(selection.TestProject, out var alias))
            {
                var classes = objectiveNames.Contains(selection.TestClass) ? objectiveClasses : obligationClasses;
                classes.Add((alias!, selection.TestClass));
            }
        }

        context = WithAdditionalClasses(context, objectiveClasses, "objective-named coverage");
        return WithAdditionalClasses(context, obligationClasses, "gate-obligation coverage");
    }

    private static PreReviewEvidenceContext BuildMappedPreReviewEvidenceContext(
        string? candidateSha, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan,
        Action<string> log)
    {
        // Manifest scoping can replace the plan's no-build check; candidate cleanup still takes priority.
        if (changeSummary.HasGeneratedArtifacts ||
            (!plan.RequiresBuild &&
             plan.Checks.Count > 0 &&
             plan.Checks.All(check => check.Command.Count == 0)))
        {
            var generatedArtifactsBlock = changeSummary.HasGeneratedArtifacts;
            return new PreReviewEvidenceContext(
                candidateSha,
                [],
                null,
                plan.Summary,
                NoApplicableTests: !generatedArtifactsBlock,
                MappingNeedsInput: generatedArtifactsBlock,
                SourceCleanupPaths: changeSummary.Files
                    .Where(file => file.IsGeneratedArtifact)
                    .Select(file => file.Path)
                    .ToArray());
        }

        var focusedChecks = plan.Checks
            .Where(check => FindArgument(check.Command, "--filter") >= 0)
            .ToArray();
        var context = focusedChecks.Length == 0
            ? new PreReviewEvidenceContext(candidateSha, [], null, plan.Summary,
                NoApplicableTests: true, MappingNeedsInput: false)
            : BuildFocusedContext(candidateSha, plan.Summary, focusedChecks, log);

        var changedClasses = new List<(string Alias, string ClassName)>();
        foreach (var check in plan.Checks)
        {
            if (check.ChangedTestClasses.Count > 0 && ReadProject(check) is { } project &&
                PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias))
            {
                changedClasses.AddRange(check.ChangedTestClasses.Select(className => (alias!, className)));
            }
        }

        context = WithAdditionalClasses(context, changedClasses, "changed-test coverage");
        return context.NoApplicableTests && focusedChecks.Length == 0
            ? context with
            {
                MappingReason = $"{context.MappingReason} No filtered test target mapped; project-wide checks are deferred to the acceptance gate."
            }
            : context;
    }

    private static PreReviewEvidenceContext BuildFocusedContext(
        string? candidateSha, string planSummary, IReadOnlyList<RepositoryTestImpactCheck> focusedChecks, Action<string> log)
    {
        var selected = focusedChecks.Select(check => check.CommandLine).ToArray();
        var requests = new List<string>();
        var notApplicableChecks = new List<string>();
        string WithNotApplicableChecks(string reason) => notApplicableChecks.Count == 0
            ? reason
            : $"{reason} {string.Join(" ", notApplicableChecks)}";

        foreach (var check in focusedChecks)
        {
            var filterIndex = FindArgument(check.Command, "--filter");
            var project = ReadProject(check);
            if (string.IsNullOrWhiteSpace(project))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks(planSummary),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (!PreReviewFocusedRequestSplitter.TryResolveBrokerAlias(project, out var alias))
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks($"Mapped project is not supported by the focused evidence broker: {project}"),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }
            if (filterIndex >= 0 && filterIndex + 1 >= check.Command.Count)
            {
                return new PreReviewEvidenceContext(
                    candidateSha,
                    selected,
                    null,
                    WithNotApplicableChecks($"Mapped test command has an empty --filter argument: {check.CommandLine}"),
                    NoApplicableTests: false,
                    MappingNeedsInput: true);
            }

            if (PreReviewExclusionOnlyFilter.IsExclusionOnly(check.Command[filterIndex + 1]))
            {
                var decision = $"check=\"{check.Name}\" reason=\"{PreReviewExclusionOnlyFilter.NotApplicableReason}\"";
                notApplicableChecks.Add($"not-applicable {decision}");
                log($"PRE_REVIEW_CHECK_NOT_APPLICABLE candidate={candidateSha} {decision}");
                continue;
            }

            if (!PreReviewFocusedRequestSplitter.TrySplitRequestItems(
                    alias!, check.Command[filterIndex + 1], out var requestItems))
            {
                return new PreReviewEvidenceContext(
                    candidateSha, selected, null,
                    WithNotApplicableChecks("Mapped test filter cannot be split into broker-safe positive clauses."),
                    NoApplicableTests: false, MappingNeedsInput: true);
            }
            requests.AddRange(requestItems);
        }

        var noApplicableTests = requests.Count == 0 && notApplicableChecks.Count > 0;
        return new PreReviewEvidenceContext(
            candidateSha,
            requests,
            noApplicableTests ? null : string.Join("; ", requests),
            WithNotApplicableChecks(planSummary),
            NoApplicableTests: noApplicableTests,
            MappingNeedsInput: requests.Count == 0 && !noApplicableTests);
    }

    private static string DescribeUnmappedTestSources(
        string mappingReason, RepositoryChangeSummary changeSummary, RepositoryTestImpactPlan plan)
    {
        var paths = changeSummary.Files
            .Where(file => file.Categories.Contains(RepositoryChangeCategory.Test) &&
                file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Path)
            .ToArray();
        if (paths.Length == 0) return mappingReason;

        var reasons = plan.Checks
            .Where(check => check.TestProject is not null && check.Reason != plan.Summary &&
                FindArgument(check.Command, "--filter") < 0)
            .Select(check => check.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var files = $"Changed test sources were present. Unmapped test sources include {FormatDiagnosticItems(paths, 5)}.";
        return reasons.Length == 0
            ? $"{mappingReason} {files}"
            : $"{mappingReason} {files} Planner reasons include {FormatDiagnosticItems(reasons, 3)}";
    }

    private static string FormatDiagnosticItems(string[] items, int limit) =>
        string.Join(" / ", items.Take(limit)) + (items.Length > limit ? $" (+{items.Length - limit} more)" : "");

    private static string? ReadProject(RepositoryTestImpactCheck check) =>
        check.Command.FirstOrDefault(argument => argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

    private static int FindArgument(IReadOnlyList<string> arguments, string value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
