using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ImpactReplayChangedTestClassSelection
{
    internal static (IReadOnlyList<string> Classes, bool Gained, string? AbandonReason, IReadOnlyList<string> OtherFiles,
        IReadOnlyList<string> QualifiedClasses) Read(
        string checkout, IReadOnlyList<string> changedFiles, IReadOnlyList<string> reverseDependentClasses,
        ImpactReplayChangedSourceRouting routing)
    {
        // Deleted files declare no classes at the attempt's revision.
        var changed = ImpactReplayChangedTestSupportSelection.Read(checkout, changedFiles, routing);
        var classes = new SortedSet<string>(reverseDependentClasses, StringComparer.Ordinal);
        var previousCount = classes.Count;
        classes.UnionWith(changed.Classes.Select(name => name.Split('.', '+').Last()));
        return (classes.ToArray(), classes.Count > previousCount, changed.AbandonReason, changed.OtherFiles, changed.Classes);
    }
}
