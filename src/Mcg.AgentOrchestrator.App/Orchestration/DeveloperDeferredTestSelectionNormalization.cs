using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeveloperDeferredTestSelectionNormalization
{
    internal static DeveloperDeferredTestSelection Normalize(
        DeveloperDeferredTestSelection declaration, AcceptanceGateEngineSettings settings)
    {
        var selected = new List<FindingEvidenceSelection>();
        var notRun = new List<string>(declaration.NotRun);
        foreach (var selection in declaration.Selections)
        {
            if (FindingEvidenceRequestNormalization.TryNormalize(
                    new FindingEvidenceRequest([selection]), settings, (_, _) => [],
                    out var normalized, out _, out _, out _))
                selected.AddRange(normalized.Selections);
            else
                notRun.Add(selection.TestClass);
        }
        return new DeveloperDeferredTestSelection(selected, notRun);
    }

    internal static void AddOpenFindingSelections(List<FindingEvidenceSelection> selected,
        Goal goal, AcceptanceGateEngineSettings settings, Func<string, string, IReadOnlyList<string>> resolveSiblingClasses)
    {
        foreach (var finding in goal.Tasks
                     .Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
                     .SelectMany(task => task.LastVerification?.MergedReviewFindings ?? [])
                     .Where(finding => finding.State == ReviewFindingState.Open && finding.EvidenceRequest is not null))
        {
            foreach (var selection in finding.EvidenceRequest!.Selections)
            {
                if (FindingEvidenceRequestNormalization.TryNormalize(
                        new FindingEvidenceRequest([selection]), settings, resolveSiblingClasses,
                        out var normalized, out _, out _, out _))
                    selected.AddRange(normalized.Selections);
            }
        }
    }
}
