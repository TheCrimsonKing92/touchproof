using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplaySelectionAugmentation
{
    internal static ReverseDependencyTestSelection Apply(ReverseDependencyTestSelection selection,
        IEnumerable<string> changedFiles, ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, ImpactReplaySourceGuardReport? guards,
        IReadOnlyList<string> testProjectDirectories)
        => Apply(selection, changedFiles, sources, classes, guards, out _, null, testProjectDirectories);

    internal static ReverseDependencyTestSelection Apply(ReverseDependencyTestSelection selection,
        IEnumerable<string> changedFiles, ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, ImpactReplaySourceGuardReport? guards,
        out ImpactReplayDeclaredGuardReport declaredGuards, IReadOnlyList<string>? additionalClasses,
        IReadOnlyList<string> testProjectDirectories)
    {
        var changed = changedFiles.ToArray();
        declaredGuards = ImpactReplayDeclaredGuardSelection.Read(changed, sources, classes);
        var mapped = ChangedFileTestClassPathMapper.Read(changed, sources, classes, testProjectDirectories);
        if (mapped.Outcome != ReverseDependencySelectionOutcome.Resolved) return mapped;
        if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved) return selection;
        if (declaredGuards.DegradationReason is { } reason)
            return ReverseDependencyTestSelection.Unreadable(reason, selection.CacheReceipt, selection.Headroom);
        var names = selection.TestClassNames.Concat(mapped.TestClassNames).Concat(guards?.Classes ?? [])
            .Concat(declaredGuards.Selections.Select(item => item.ClassName)).Concat(additionalClasses ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Count distinct runnable identities; a reverse-dependency short name and a declared full
        // name can denote the same class. Never truncate required guards to fit a focused filter.
        var count = names.SelectMany(name =>
        {
            var matches = classes.Where(item => name.Contains('.') || name.Contains('+')
                    ? item.FullName == name : item.FullName.Split('.', '+').Last() == name)
                .Select(item => item.FullName).ToArray();
            return matches.Length == 0 ? [name] : matches;
        }).Distinct(StringComparer.Ordinal).Count();
        var headroom = selection.Headroom is { } prior ? prior with { SelectedTestClassCount = count } : null;
        return selection with { TestClassNames = names, Headroom = headroom };
    }
}
