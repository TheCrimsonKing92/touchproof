using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using FocusedEvidencePlannedCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidencePlannedCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class FocusedEvidenceManifestChecks(AcceptanceGateEngineSettings engineSettings, string worktreePath)
{
    private readonly HashSet<string> _ambiguousProjectLabels = DeclaredTestProjectInventory
        .DeclaredProjects(engineSettings, engineSettings?.DeclaredDotnetTestChecks)
        .GroupBy(GoalAcceptanceVerifier.ProjectLabel, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static string FocusedTestCheckName(string projectLabel, string filter) =>
        $"reviewer focused evidence: {projectLabel} {filter}";

    internal AcceptanceManifestCheck Create(
        FocusedEvidencePlannedCheck item,
        IReadOnlyList<FocusedEvidenceMatchedClassSelection> matchedClasses,
        int targetCount)
    {
        var usesDeclaredVstestCheck = VstestFocusedEvidenceCheckShape.TryResolve(
            item.Project, engineSettings, out var declaringCheck);
        var label = GoalAcceptanceVerifier.ProjectLabel(item.Project);
        var focusedLabel = _ambiguousProjectLabels.Contains(label) ? item.Project : label;
        return new AcceptanceManifestCheck
        {
            Name = item.Filter is null
                ? $"reviewer mapped project evidence: {label}"
                : FocusedTestCheckName(focusedLabel, item.Filter.CanonicalText),
            Type = "dotnet-test",
            Runner = usesDeclaredVstestCheck
                ? "vstest"
                : GoalAcceptanceVerifier.ResolveDotnetTestRunner(worktreePath, item.Project),
            Project = item.Project,
            Arguments = usesDeclaredVstestCheck
                ? VstestFocusedEvidenceCheckShape.Arguments(declaringCheck, item.Filter?.CanonicalText)
                : item.Filter is null
                ? ["--verbosity", "minimal"]
                : ["--verbosity", "minimal", "--filter", item.Filter.CanonicalText],
            FocusedEvidenceTokens = item.Filter?.Tokens ?? [],
            FocusedEvidenceSelections = item.SelectionFilters
                .Select(filter => filter.Tokens)
                .ToArray(),
            IsFocusedEvidenceSelection = item.Filter is not null,
            FocusedEvidenceMatchedClasses = matchedClasses,
            TimeoutMinutes = usesDeclaredVstestCheck
                ? declaringCheck.TimeoutMinutes
                : targetCount <= GoalAcceptanceVerifier.FocusedEvidenceShortTimeoutTargetLimit ? 10 : null
        };
    }
}
