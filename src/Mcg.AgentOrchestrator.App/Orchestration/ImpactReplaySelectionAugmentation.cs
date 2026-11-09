using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ImpactReplaySelectionAugmentation
{
    internal static ReverseDependencyTestSelection Apply(ReverseDependencyTestSelection selection,
        IEnumerable<string> changedFiles, ImpactReplayTestSourceIndex sources,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, ImpactReplaySourceGuardReport? guards)
    {
        var mapped = ChangedFileTestClassPathMapper.Read(changedFiles, sources, classes);
        if (mapped.Outcome != ReverseDependencySelectionOutcome.Resolved) return mapped;
        if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved) return selection;
        return selection with { TestClassNames = selection.TestClassNames.Concat(mapped.TestClassNames)
            .Concat(guards?.Classes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() };
    }
}
