using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record FocusedEvidenceDroppedSelection(
    string Target, string Project, string Selection, IReadOnlyList<string> Reasons);

// A drop is a planning decision backed by positive discovery, never an inference from empty TRX.
internal static class FocusedEvidenceTraitExcludedSelection
{
    internal static List<(string Target, string Project, FocusedEvidenceFilter? Filter)> Apply(
        string worktreePath, AcceptanceGateEngineSettings settings,
        IReadOnlyList<(string Target, string Project, FocusedEvidenceFilter? Filter)> validated,
        out IReadOnlyList<FocusedEvidenceDroppedSelection> dropped)
    {
        var result = new List<(string Target, string Project, FocusedEvidenceFilter? Filter)>();
        var omissions = new List<FocusedEvidenceDroppedSelection>();
        var discoveries = new Dictionary<string, FocusedEvidenceTraitExclusionDiscovery?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in validated)
        {
            if (item.Filter is not { } filter ||
                !filter.Tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.Class) ||
                filter.Tokens.Any(token => token.Kind == FocusedEvidenceTokenKind.Method))
            {
                result.Add(item);
                continue;
            }

            var exclusions = filter.Tokens.Where(token => token.Kind == FocusedEvidenceTokenKind.ExcludedTrait)
                .Select(token => (Key: "Category", Value: token.Value)).ToList();
            var usesVstest = VstestFocusedEvidenceCheckShape.TryResolve(item.Project, settings, out var declaringCheck);
            // A declaring VSTest filter is an additional constraint outside this resolved arm.
            // Without proving its non-trait match, keep the original selection and coverage check.
            if (usesVstest && declaringCheck.Arguments.Any(argument =>
                argument.Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(item);
                continue;
            }
            var probe = new AcceptanceManifestCheck { Project = item.Project,
                Runner = usesVstest ? "vstest" : ResolveDotnetTestRunner(worktreePath, item.Project) };
            if (AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(probe) &&
                AcceptanceCheckCommandBuilder.NeedsUnattendedHostIntegrationExclusion(probe))
                exclusions.Add(("Category", "HostIntegration"));
            if (exclusions.Count == 0)
            {
                result.Add(item);
                continue;
            }

            if (!discoveries.TryGetValue(item.Project, out var discovery))
                discoveries[item.Project] = discovery = FocusedEvidenceTraitExclusionDiscovery.Read(worktreePath, item.Project);
            var retained = new List<FocusedEvidenceFilterToken>();
            foreach (var token in filter.Tokens)
            {
                var reasons = token.Kind == FocusedEvidenceTokenKind.Class
                    ? discovery?.ProveExcluded(token.Value, filter.Tokens, exclusions) : null;
                if (reasons is not { Count: > 0 })
                    retained.Add(token);
                else
                    omissions.Add(new(item.Target, item.Project, token.Value, reasons));
            }
            if (retained.Count == filter.Tokens.Count)
                result.Add(item);
            else if (retained.Any(token => token.Kind == FocusedEvidenceTokenKind.Class))
                result.Add((item.Target, item.Project, filter with { Tokens = retained,
                    CanonicalText = FocusedEvidenceRequestResolver.CanonicalLaunchFilter(retained) }));
        }
        dropped = omissions;
        return result;
    }

    internal static FocusedEvidenceCoverage CreateCoverage(
        IReadOnlyList<FocusedEvidenceTargetCoverage> targetToChecks,
        string executionMode, string executionReason,
        IReadOnlyList<FocusedEvidenceDroppedSelection> dropped)
    {
        var noRunnableSelection = targetToChecks.Count == 0 && dropped.Count > 0;
        return new(targetToChecks,
            ExecutionMode: noRunnableSelection ? "no-runnable-selection" : executionMode,
            ExecutionReason: noRunnableSelection ? "no-runnable-selection" : executionReason,
            DroppedSelections: dropped.Count == 0 ? null : dropped);
    }

    internal static FocusedEvidenceRunResult NoRunnableSelection(string request, FocusedEvidenceCoverage coverage) =>
        new(request, Accepted: true, Passed: false,
            Summary: "no runnable selection" + FormatDropped(coverage), Checks: [], Coverage: coverage,
            OutcomeReason: FindingEvidenceOutcomeReason.CandidateInconclusive);

    internal static string FormatDropped(FocusedEvidenceCoverage coverage) =>
        coverage.DroppedSelections is not { Count: > 0 } dropped ? string.Empty :
            "; dropped: " + string.Join("; ", dropped.Select(item =>
                $"{item.Project}:{item.Selection} ({string.Join(',', item.Reasons)})"));
}
