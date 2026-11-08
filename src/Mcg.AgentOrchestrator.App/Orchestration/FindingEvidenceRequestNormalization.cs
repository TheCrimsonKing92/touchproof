using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorDriver;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceRequestNormalization
{
    private static readonly GoalRepositorySet HomeRepository = new(["default"]);
    internal static bool IsHomeRepository(string? key) => key is null || HomeRepository.Contains(key);

    internal static bool TryNormalize(
        FindingEvidenceRequest request,
        AcceptanceGateEngineSettings engineSettings,
        Func<string, string, IReadOnlyList<string>> resolveSiblingClasses,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail)
    {
        if (!IsHomeRepository(request.Repository))
        {
            normalized = new FindingEvidenceRequest([]);
            executorRequest = string.Empty;
            refusalReason = FindingEvidenceNotHonouredReason.UnsupportedProject;
            refusalDetail = "Repository-keyed evidence resolves only through the goal-aware finding-evidence path.";
            return false;
        }
        return TryNormalizeCore(request, engineSettings, resolveSiblingClasses,
            out normalized, out executorRequest, out refusalReason, out refusalDetail);
    }

    internal static bool TryNormalizeForGoal(
        FindingEvidenceRequest request,
        Goal goal,
        AcceptanceGateEngineSettings homeSettings,
        Func<Goal, string, (KeyedRepositoryEvidenceTarget? Target, string Failure)> resolveTarget,
        Func<string, string, IReadOnlyList<string>> resolveSiblingClasses,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out KeyedRepositoryEvidenceTarget? target,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail)
    {
        target = null;
        if (IsHomeRepository(request.Repository))
            return TryNormalizeCore(request, homeSettings, resolveSiblingClasses,
                out normalized, out executorRequest, out refusalReason, out refusalDetail);

        normalized = new FindingEvidenceRequest([]);
        executorRequest = string.Empty;
        if (goal.Repositories?.Repositories.Contains(request.Repository) != true)
        {
            refusalReason = FindingEvidenceNotHonouredReason.UnsupportedProject;
            var keys = goal.Repositories is null ? "none (no keys are declared)" : string.Join(", ", goal.Repositories.Keys);
            refusalDetail = $"Repository '{request.Repository}' is not declared on the goal. Declared keys: {keys}.";
            return false;
        }

        var resolution = resolveTarget(goal, request.Repository!);
        target = resolution.Target;
        if (target is null)
        {
            refusalReason = FindingEvidenceNotHonouredReason.ExecutorUnavailable;
            refusalDetail = $"Repository '{request.Repository}' evidence target is unavailable: {resolution.Failure}; home resources are never substituted.";
            return false;
        }
        return TryNormalizeCore(request, target.EngineSettings, (_, _) => [],
            out normalized, out executorRequest, out refusalReason, out refusalDetail);
    }

    private static bool TryNormalizeCore(
        FindingEvidenceRequest request,
        AcceptanceGateEngineSettings engineSettings,
        Func<string, string, IReadOnlyList<string>> resolveSiblingClasses,
        out FindingEvidenceRequest normalized,
        out string executorRequest,
        out FindingEvidenceNotHonouredReason refusalReason,
        out string refusalDetail)
    {
        normalized = new FindingEvidenceRequest([]);
        executorRequest = string.Empty;
        refusalReason = FindingEvidenceNotHonouredReason.UnparseableSelection;
        refusalDetail = "Evidence request must contain at least one project/class selection.";
        if (request.Selections is not { Count: > 0 })
        {
            return false;
        }

        var crosswalk = CrosswalkFocusedEvidence.Applies(engineSettings);
        var nextest = RustNextestFocusedEvidence.Applies(engineSettings);
        var selections = new List<FindingEvidenceSelection>();
        foreach (var selection in request.Selections)
        {
            if (selection is null)
            {
                refusalDetail = "Evidence selections cannot contain null entries.";
                return false;
            }
            var project = selection.TestProject?.Trim();
            var originalTestClass = crosswalk || nextest ? selection.TestClass ?? string.Empty
                : FindingEvidenceNameSelector.ToFullyQualified(selection.TestClass ?? string.Empty);
            var testClass = originalTestClass.Trim();
            if (string.IsNullOrWhiteSpace(project) ||
                string.IsNullOrWhiteSpace(testClass) ||
                originalTestClass.Length > FindingEvidenceFilterSyntax.MaximumLength ||
                !(crosswalk ? CrosswalkFocusedEvidence.IsSupportedSelector(testClass)
                    : nextest ? RustNextestFocusedEvidence.IsSupportedSelector(originalTestClass)
                    : FindingEvidenceFilterSyntax.IsSupported(testClass) || GoPackageFocusedEvidence.Applies(engineSettings)))
            {
                refusalDetail =
                    $"Every evidence selection requires a bounded valid test_project and test_class; " +
                    $"offending_filter='{originalTestClass}'.";
                return false;
            }

            if (!GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(
                    project,
                    engineSettings,
                    out var resolvedProject))
            {
                refusalReason = FindingEvidenceNotHonouredReason.UnsupportedProject;
                refusalDetail =
                    $"Focused evidence does not support test project '{project}'. Accepted forms: " +
                    $"{GoalAcceptanceVerifier.FocusedEvidenceSupportedProjectForms(engineSettings)}.";
                return false;
            }

            var canonicalProject = GoalAcceptanceVerifier.ProjectLabel(resolvedProject);
            // Keep the executor's selector syntax. Set expansion belongs to coverage planning,
            // not to request delivery (in particular Name operands and Boolean filters).
            IReadOnlyList<string> classFilters = [originalTestClass];
            foreach (var classFilter in classFilters)
            {
                selections.Add(new FindingEvidenceSelection(canonicalProject, classFilter));
                if (crosswalk || nextest || !FindingEvidenceFilterSyntax.TryGetExpandableClass(classFilter.Trim(), out var requestedClass, out var filterPrefix)) continue;
                foreach (var siblingClass in resolveSiblingClasses(resolvedProject, requestedClass))
                {
                    if (string.IsNullOrWhiteSpace(siblingClass) ||
                        !FindingEvidenceFilterSyntax.IsExpandableClassName(siblingClass))
                    {
                        continue;
                    }

                    selections.Add(new FindingEvidenceSelection(
                        canonicalProject,
                        filterPrefix + siblingClass));
                }
            }
        }

        var distinct = selections
            .Distinct()
            .OrderBy(selection => selection.TestProject, StringComparer.Ordinal)
            .ThenBy(selection => selection.TestClass, StringComparer.Ordinal)
            .ToArray();
        normalized = request with { Selections = distinct };
        executorRequest = string.Join(
            "; ",
            distinct.Select(FormatFindingEvidenceSelection));
        return true;
    }
}
