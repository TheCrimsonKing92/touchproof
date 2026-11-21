using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ImpactReplayChangedTestClassSelection
{
    internal static (IReadOnlyList<string> Classes, bool Gained, string? AbandonReason, IReadOnlyList<string> OtherFiles,
        IReadOnlyList<string> QualifiedClasses, ImpactReplayChangedSupportSelection Support) Read(
        string checkout, IReadOnlyList<string> changedFiles, IReadOnlyList<string> reverseDependentClasses,
        ImpactReplayChangedSourceRouting routing, int threshold = ImpactReplayChangedSupportReferenceScan.DefaultClosureThreshold)
    {
        var changed = ImpactReplayChangedTestSupportSelection.Read(checkout, changedFiles, routing, threshold);
        var classes = new SortedSet<string>(reverseDependentClasses, StringComparer.Ordinal);
        var previousCount = classes.Count;
        classes.UnionWith(changed.Classes.Select(name => name.Split('.', '+').Last()));
        return (classes.ToArray(), classes.Count > previousCount, changed.AbandonReason, changed.OtherFiles, changed.Classes, changed);
    }
}
